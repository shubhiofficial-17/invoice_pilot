using System.Threading.Channels;
using InvoicePilot.Api.Ai;
using InvoicePilot.Api.Invoices;
using InvoicePilot.Api.Matching;
using InvoicePilot.Api.Policy;

namespace InvoicePilot.Api.Agent;

/// <summary>Invoices that have been read and are waiting for the agent.</summary>
public sealed class AgentQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();

    public void Enqueue(string invoiceId) => _channel.Writer.TryWrite(invoiceId);

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Extracted → Investigating → AwaitingReview (or InvestigationFailed), version-checked at each step.</summary>
public sealed class AgentWorker(
    AgentQueue queue,
    IInvoiceStore store,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    IConfiguration config,
    ILogger<AgentWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var status in new[] { InvoiceStatus.Investigating, InvoiceStatus.Extracted })
        {
            foreach (var invoice in await store.ListByStatusAsync(status, stoppingToken)) queue.Enqueue(invoice.Id);
        }

        var concurrency = Math.Clamp(config.GetValue("Agent:Concurrency", 2), 1, 16);
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
                    log.LogError(e, "Agent run for {InvoiceId} crashed", id);
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
        if (invoice?.Extraction is null || invoice.Status is not (InvoiceStatus.Extracted or InvoiceStatus.Investigating)) return;

        var claimed = await store.TryUpdateAsync(invoice with { Status = InvoiceStatus.Investigating, UpdatedAt = Now() }, ct);
        if (claimed is null) return;

        await using var scope = scopes.CreateAsyncScope();
        var llm = scope.ServiceProvider.GetRequiredService<ILlmClient>();
        var matcher = scope.ServiceProvider.GetRequiredService<MatchService>();
        var decider = scope.ServiceProvider.GetRequiredService<DecisionService>();
        Invoice next;
        if (!llm.Enabled)
        {
            // No agent, but the deterministic match and policy still give the clerk a recommendation.
            var match = await matcher.MatchAsync(claimed.Extraction!, ct);
            next = claimed with
            {
                Status = InvoiceStatus.InvestigationFailed,
                FailureReason = "The agent is turned off: no AI endpoint is configured (Ai:BaseUrl).",
                Match = match,
                Decision = await decider.DecideAsync(match, null, ct),
            };
        }
        else
        {
            var agent = scope.ServiceProvider.GetRequiredService<InvoiceAgent>();
            var run = await agent.RunAsync(claimed, ct);
            // The guardrail: re-run the match (the agent's view doesn't count) and apply the policy.
            var match = await matcher.MatchAsync(claimed.Extraction!, ct);
            var decision = await decider.DecideAsync(match, run.Status == AgentRunStatus.Completed ? run.Proposal : null, ct);
            next = run.Status == AgentRunStatus.Completed
                ? claimed with { Status = InvoiceStatus.AwaitingReview, Agent = run, Match = match, Decision = decision, FailureReason = null }
                : claimed with { Status = InvoiceStatus.InvestigationFailed, Agent = run, Match = match, Decision = decision, FailureReason = run.FailureReason };
            log.LogInformation("Agent run for {InvoiceId}: {Status}, {Outcome}, {Calls} model calls, {Tokens} tokens",
                id, run.Status, run.Proposal?.Outcome, run.ModelCalls, run.Usage.PromptTokens + run.Usage.CompletionTokens);
        }

        if (await store.TryUpdateAsync(next with { UpdatedAt = Now() }, ct) is null)
        {
            log.LogWarning("Invoice {InvoiceId} changed during the agent run; result discarded", id);
        }
    }

    private DateTimeOffset Now()
    {
        var utc = clock.GetUtcNow();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
    }
}
