using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvoicePilot.Api.Erp.Mongo;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace InvoicePilot.Api.Audit;

/// <summary>
/// One entry in an invoice's audit trail. Entries are hash-chained per invoice: each hash covers the
/// entry's content and the previous entry's hash, so editing or deleting an old entry breaks the chain.
/// </summary>
public sealed record AuditEvent(
    string InvoiceId,
    int Seq,
    DateTimeOffset At,
    string Actor,
    string Type,
    string Summary,
    IReadOnlyDictionary<string, string> Data,
    string PrevHash,
    string Hash);

public sealed record AuditVerification(bool Intact, int? BrokenAtSeq, int Count);

public interface IAuditLog
{
    Task<AuditEvent> AppendAsync(string invoiceId, string actor, string type, string summary,
        IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEvent>> ListAsync(string invoiceId, CancellationToken ct = default);
}

public static class AuditChain
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string HashOf(string invoiceId, int seq, DateTimeOffset at, string actor, string type, string summary,
        IReadOnlyDictionary<string, string> data, string prevHash)
    {
        // Canonical form: fixed field order, data keys sorted, timestamps in UTC to the millisecond.
        var canonical = JsonSerializer.Serialize(new object[]
        {
            invoiceId, seq, at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), actor, type, summary,
            data.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new[] { kv.Key, kv.Value }).ToArray(),
            prevHash,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static AuditVerification Verify(IReadOnlyList<AuditEvent> events)
    {
        var prev = Genesis;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var expected = HashOf(e.InvoiceId, e.Seq, e.At, e.Actor, e.Type, e.Summary, e.Data, prev);
            if (e.Seq != i + 1 || e.PrevHash != prev || e.Hash != expected) return new(false, e.Seq, events.Count);
            prev = e.Hash;
        }
        return new(true, null, events.Count);
    }

    internal static DateTimeOffset Millis(DateTimeOffset t) =>
        new(t.UtcTicks - t.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    internal static IReadOnlyDictionary<string, string> Clean(IReadOnlyDictionary<string, string>? data) =>
        (data ?? new Dictionary<string, string>())
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Length > 1000 ? kv.Value[..1000] + "…" : kv.Value);
}

public sealed class InMemoryAuditLog(TimeProvider clock) : IAuditLog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<AuditEvent>> _events = new();

    public Task<AuditEvent> AppendAsync(string invoiceId, string actor, string type, string summary,
        IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _events.TryGetValue(invoiceId, out var l) ? l : _events[invoiceId] = [];
            var prev = list.Count == 0 ? AuditChain.Genesis : list[^1].Hash;
            var at = AuditChain.Millis(clock.GetUtcNow());
            var clean = AuditChain.Clean(data);
            var seq = list.Count + 1;
            var e = new AuditEvent(invoiceId, seq, at, actor, type, summary, clean, prev,
                AuditChain.HashOf(invoiceId, seq, at, actor, type, summary, clean, prev));
            list.Add(e);
            return Task.FromResult(e);
        }
    }

    public Task<IReadOnlyList<AuditEvent>> ListAsync(string invoiceId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AuditEvent>>(_events.TryGetValue(invoiceId, out var l) ? l.ToList() : []);
        }
    }

    /// <summary>For tests: change a stored entry as an attacker with database access would.</summary>
    public void Tamper(string invoiceId, int seq, Func<AuditEvent, AuditEvent> change)
    {
        lock (_gate)
        {
            var list = _events[invoiceId];
            list[seq - 1] = change(list[seq - 1]);
        }
    }
}

public sealed class AuditEventDoc
{
    [BsonId] public string Id { get; set; } = "";
    public string InvoiceId { get; set; } = "";
    public int Seq { get; set; }
    public DateTime At { get; set; }
    public string Actor { get; set; } = "";
    public string Type { get; set; } = "";
    public string Summary { get; set; } = "";
    public Dictionary<string, string> Data { get; set; } = new();
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";

    public AuditEvent ToModel() => new(InvoiceId, Seq, new DateTimeOffset(DateTime.SpecifyKind(At, DateTimeKind.Utc)),
        Actor, Type, Summary, Data, PrevHash, Hash);
}

/// <summary>
/// Append-only: this class has no update or delete. The unique (invoiceId, seq) index makes two
/// concurrent appends to the same invoice collide; the loser re-reads the head and retries.
/// </summary>
public sealed class MongoAuditLog : IAuditLog
{
    private readonly IMongoCollection<AuditEventDoc> _events;
    private readonly TimeProvider _clock;

    public MongoAuditLog(IMongoDatabase db, TimeProvider clock)
    {
        MongoSetup.Configure();
        _events = db.GetCollection<AuditEventDoc>("auditEvents");
        _clock = clock;
    }

    public IMongoCollection<AuditEventDoc> Collection => _events;

    public Task EnsureIndexesAsync(CancellationToken ct = default) =>
        _events.Indexes.CreateOneAsync(new CreateIndexModel<AuditEventDoc>(
            Builders<AuditEventDoc>.IndexKeys.Ascending(e => e.InvoiceId).Ascending(e => e.Seq),
            new CreateIndexOptions { Unique = true }), cancellationToken: ct);

    public async Task<AuditEvent> AppendAsync(string invoiceId, string actor, string type, string summary,
        IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default)
    {
        var clean = AuditChain.Clean(data);
        for (var attempt = 0; ; attempt++)
        {
            var head = await _events.Find(e => e.InvoiceId == invoiceId).SortByDescending(e => e.Seq).Limit(1).FirstOrDefaultAsync(ct);
            var seq = (head?.Seq ?? 0) + 1;
            var prev = head?.Hash ?? AuditChain.Genesis;
            var at = AuditChain.Millis(_clock.GetUtcNow());
            var hash = AuditChain.HashOf(invoiceId, seq, at, actor, type, summary, clean, prev);
            var doc = new AuditEventDoc
            {
                Id = $"{invoiceId}:{seq}", InvoiceId = invoiceId, Seq = seq, At = at.UtcDateTime, Actor = actor,
                Type = type, Summary = summary, Data = clean.ToDictionary(), PrevHash = prev, Hash = hash,
            };
            try
            {
                await _events.InsertOneAsync(doc, cancellationToken: ct);
                return doc.ToModel();
            }
            catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey && attempt < 10)
            {
                await Task.Delay(Random.Shared.Next(2, 10) * (attempt + 1), ct);
            }
        }
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAsync(string invoiceId, CancellationToken ct = default)
    {
        var docs = await _events.Find(e => e.InvoiceId == invoiceId).SortBy(e => e.Seq).ToListAsync(ct);
        return docs.Select(d => d.ToModel()).ToList();
    }
}

public sealed class AuditInitializer(IAuditLog log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (log is MongoAuditLog mongo) await mongo.EnsureIndexesAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Small helpers so call sites read like sentences.</summary>
public static class AuditData
{
    public static IReadOnlyDictionary<string, string> Of(params (string Key, object? Value)[] pairs) =>
        pairs.Where(p => p.Value is not null)
            .ToDictionary(p => p.Key, p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
}
