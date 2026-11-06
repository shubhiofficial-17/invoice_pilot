using InvoicePilot.Api.Erp.Mongo;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace InvoicePilot.Api.Invoices.Mongo;

/// <summary>
/// Invoice metadata lives in "invoices"; file bytes live in "invoiceFiles" (one document per
/// invoice, capped at 10 MB by the upload endpoint, well under Mongo's 16 MB document limit),
/// so listing invoices never loads the files.
/// </summary>
public sealed class MongoInvoiceStore : IInvoiceStore
{
    private readonly IMongoCollection<InvoiceDoc> _invoices;
    private readonly IMongoCollection<InvoiceFileDoc> _files;

    public MongoInvoiceStore(IMongoDatabase db)
    {
        MongoSetup.Configure();
        _invoices = db.GetCollection<InvoiceDoc>("invoices");
        _files = db.GetCollection<InvoiceFileDoc>("invoiceFiles");
    }

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await _invoices.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<InvoiceDoc>(
                Builders<InvoiceDoc>.IndexKeys.Ascending(i => i.File.Sha256),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<InvoiceDoc>(Builders<InvoiceDoc>.IndexKeys.Descending(i => i.ReceivedAt)),
            new CreateIndexModel<InvoiceDoc>(Builders<InvoiceDoc>.IndexKeys.Ascending(i => i.Status)),
        ], ct);
    }

    public async Task<(Invoice Invoice, bool Created)> AddAsync(Invoice invoice, byte[] content, CancellationToken ct = default)
    {
        var existing = await FindByShaAsync(invoice.File.Sha256, ct);
        if (existing is not null) return (existing, false);

        // File first: if the metadata insert then loses a race, the orphan file is overwritten
        // or never referenced, and no invoice ever points at a missing file.
        await _files.ReplaceOneAsync(
            f => f.Id == invoice.Id,
            new InvoiceFileDoc { Id = invoice.Id, Content = content },
            new ReplaceOptions { IsUpsert = true },
            ct);
        try
        {
            await _invoices.InsertOneAsync(InvoiceDoc.From(invoice), cancellationToken: ct);
            return (invoice, true);
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Someone uploaded the same file at the same moment.
            await _files.DeleteOneAsync(f => f.Id == invoice.Id, ct);
            var winner = await FindByShaAsync(invoice.File.Sha256, ct)
                ?? throw new InvalidOperationException("Duplicate key without a matching invoice.");
            return (winner, false);
        }
    }

    public async Task<Invoice?> GetAsync(string id, CancellationToken ct = default)
    {
        var doc = await _invoices.Find(i => i.Id == id).FirstOrDefaultAsync(ct);
        return doc?.ToModel();
    }

    public async Task<IReadOnlyList<Invoice>> ListAsync(int limit = 100, CancellationToken ct = default)
    {
        var docs = await _invoices.Find(FilterDefinition<InvoiceDoc>.Empty)
            .SortByDescending(i => i.ReceivedAt)
            .ThenByDescending(i => i.Id)
            .Limit(limit)
            .ToListAsync(ct);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<Invoice>> ListByStatusAsync(InvoiceStatus status, CancellationToken ct = default)
    {
        var docs = await _invoices.Find(i => i.Status == status).SortBy(i => i.ReceivedAt).ToListAsync(ct);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public async Task<byte[]?> GetFileAsync(string id, CancellationToken ct = default)
    {
        var doc = await _files.Find(f => f.Id == id).FirstOrDefaultAsync(ct);
        return doc?.Content;
    }

    public async Task<Invoice?> TryUpdateAsync(Invoice invoice, CancellationToken ct = default)
    {
        var saved = invoice with { Version = invoice.Version + 1 };
        var result = await _invoices.ReplaceOneAsync(
            i => i.Id == invoice.Id && i.Version == invoice.Version,
            InvoiceDoc.From(saved),
            cancellationToken: ct);
        return result.MatchedCount == 1 ? saved : null;
    }

    private async Task<Invoice?> FindByShaAsync(string sha256, CancellationToken ct)
    {
        var doc = await _invoices.Find(i => i.File.Sha256 == sha256).FirstOrDefaultAsync(ct);
        return doc?.ToModel();
    }
}

/// <summary>
/// Stores nested snapshot records (agent traces, match reports, decisions) as BSON via System.Text.Json.
/// Numbers are written as strings, so a decimal like 4.50 comes back exactly (BSON parsing would make it a double).
/// </summary>
public static class StoredJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
            | System.Text.Json.Serialization.JsonNumberHandling.WriteAsString,
    };

    public static BsonDocument ToBson<T>(T value) => BsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(value, Options));

    public static T FromBson<T>(BsonDocument doc) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(
            doc.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson }),
            Options)!;
}

public sealed class InvoiceFileDoc
{
    [BsonId] public string Id { get; set; } = "";
    public byte[] Content { get; set; } = [];
}

public sealed class InvoiceFileInfoDoc
{
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class InvoiceDoc
{
    [BsonId] public string Id { get; set; } = "";
    [BsonRepresentation(BsonType.String)] public InvoiceStatus Status { get; set; }
    public InvoiceFileInfoDoc File { get; set; } = new();
    public DateTime ReceivedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Version { get; set; }
    public ExtractionDoc? Extraction { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>The agent's trace, stored as a JSON document (no money in it; see ADR 0006).</summary>
    public BsonDocument? Agent { get; set; }

    /// <summary>Snapshot of the match the decision was based on. Amounts inside are stored as exact decimal strings.</summary>
    public BsonDocument? Match { get; set; }

    public BsonDocument? Decision { get; set; }

    public static InvoiceDoc From(Invoice i) => new()
    {
        Id = i.Id,
        Status = i.Status,
        File = new InvoiceFileInfoDoc
        {
            FileName = i.File.FileName, ContentType = i.File.ContentType, Size = i.File.Size, Sha256 = i.File.Sha256,
        },
        ReceivedAt = i.ReceivedAt.UtcDateTime,
        UpdatedAt = i.UpdatedAt.UtcDateTime,
        Version = i.Version,
        Extraction = i.Extraction is null ? null : ExtractionDoc.From(i.Extraction),
        FailureReason = i.FailureReason,
        Agent = i.Agent is null ? null : StoredJson.ToBson(i.Agent),
        Match = i.Match is null ? null : StoredJson.ToBson(i.Match),
        Decision = i.Decision is null ? null : StoredJson.ToBson(i.Decision),
    };

    public Invoice ToModel() => new(
        Id,
        Status,
        new InvoiceFile(File.FileName, File.ContentType, File.Size, File.Sha256),
        new DateTimeOffset(DateTime.SpecifyKind(ReceivedAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)),
        Version,
        Extraction?.ToModel(),
        FailureReason,
        Agent is null ? null : StoredJson.FromBson<InvoicePilot.Api.Agent.AgentRun>(Agent),
        Match is null ? null : StoredJson.FromBson<InvoicePilot.Api.Matching.MatchReport>(Match),
        Decision is null ? null : StoredJson.FromBson<InvoicePilot.Api.Policy.Decision>(Decision));
}
