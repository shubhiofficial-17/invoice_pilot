using System.Threading.Channels;
using InvoicePilot.Api.Ai;
using InvoicePilot.Api.Invoices;

namespace InvoicePilot.Api.Extraction;

/// <summary>Invoice ids waiting to be read. Uploads return immediately; reading happens in the background.</summary>
public sealed class ExtractionQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();

    public void Enqueue(string invoiceId) => _channel.Writer.TryWrite(invoiceId);

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Moves invoices from Received → Extracting → Extracted / ExtractionFailed.
/// Every status change goes through the store's version check, so a person acting on
/// the same invoice is never overwritten.
/// </summary>
public sealed class ExtractionWorker(
    ExtractionQueue queue,
    IInvoiceStore store,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    IConfiguration config,
    ILogger<ExtractionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Pick up work left over from a previous run (a crash mid-extraction leaves "Extracting").
        foreach (var status in new[] { InvoiceStatus.Extracting, InvoiceStatus.Received })
        {
            foreach (var invoice in await store.ListByStatusAsync(status, stoppingToken))
            {
                queue.Enqueue(invoice.Id);
            }
        }

        var concurrency = Math.Clamp(config.GetValue("Extraction:Concurrency", 2), 1, 16);
        var readers = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            await foreach (var id in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessAsync(id, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    log.LogError(e, "Extraction of {InvoiceId} crashed", id);
                }
            }
        }, stoppingToken));

        try
        {
            await Task.WhenAll(readers);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task ProcessAsync(string id, CancellationToken ct)
    {
        var invoice = await store.GetAsync(id, ct);
        if (invoice is null || invoice.Status is not (InvoiceStatus.Received or InvoiceStatus.Extracting)) return;

        var claimed = await store.TryUpdateAsync(
            invoice with { Status = InvoiceStatus.Extracting, FailureReason = null, UpdatedAt = Now() }, ct);
        if (claimed is null) return; // someone else changed it first

        // A scope per invoice: the LLM client is a typed HttpClient and must not be captured by this singleton.
        await using var scope = scopes.CreateAsyncScope();
        var llm = scope.ServiceProvider.GetRequiredService<ILlmClient>();
        var extractor = scope.ServiceProvider.GetRequiredService<InvoiceExtractor>();

        Invoice next;
        if (!llm.Enabled)
        {
            next = claimed with
            {
                Status = InvoiceStatus.ExtractionFailed,
                FailureReason = "Reading invoices is turned off: no AI endpoint is configured (Ai:BaseUrl).",
            };
        }
        else
        {
            var file = await store.GetFileAsync(id, ct);
            try
            {
                if (file is null) throw new ExtractionFailedException("The invoice file is missing.");
                var result = await extractor.ExtractAsync(file, claimed.File.ContentType, ct);
                next = claimed with { Status = InvoiceStatus.Extracted, Extraction = result, FailureReason = null };
                log.LogInformation("Read invoice {InvoiceId}: {Issues} issue(s), {Attempts} attempt(s)",
                    id, result.Issues.Count, result.Attempts);
            }
            catch (ExtractionFailedException e)
            {
                next = claimed with { Status = InvoiceStatus.ExtractionFailed, FailureReason = e.Message };
                log.LogWarning("Could not read invoice {InvoiceId}: {Reason}", id, e.Message);
            }
        }

        if (await store.TryUpdateAsync(next with { UpdatedAt = Now() }, ct) is null)
        {
            log.LogWarning("Invoice {InvoiceId} changed while it was being read; result discarded", id);
        }
    }

    private DateTimeOffset Now()
    {
        var utc = clock.GetUtcNow();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
    }
}
