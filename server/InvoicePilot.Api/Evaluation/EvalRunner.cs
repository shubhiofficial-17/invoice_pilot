using System.Diagnostics;
using System.Globalization;
using System.Text;
using InvoicePilot.Api.Agent;
using InvoicePilot.Api.Ai;
using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Extraction;
using InvoicePilot.Api.Invoices;
using InvoicePilot.Api.Matching;
using InvoicePilot.Api.Policy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InvoicePilot.Api.Evaluation;

/// <param name="Final">The outcome after the guardrail, or null if the pipeline failed.</param>
/// <param name="Proposed">What the agent proposed (agent mode only).</param>
public sealed record EvalCaseResult(
    string Id,
    string Category,
    Outcome Expected,
    Outcome? Final,
    Outcome? Proposed,
    bool CodesFound,
    bool Overridden,
    bool? ExtractionExact,
    int PromptTokens,
    int CompletionTokens,
    long Ms,
    string? Failure)
{
    public bool Correct => Final == Expected;

    /// <summary>The costly mistake: approving something that should not be approved.</summary>
    public bool FalseApproval => Final == Outcome.Approve && Expected != Outcome.Approve;
}

public sealed record EvalCategory(string Category, int Count, int Correct);

public sealed record EvalReport(
    string Mode,
    string Model,
    int Count,
    int Correct,
    decimal Accuracy,
    int FalseApprovals,
    int Failures,
    decimal? AgentProposalAccuracy,
    int Overrides,
    decimal? ExtractionAccuracy,
    long TotalTokens,
    decimal? EstimatedCostUsd,
    long P50Ms,
    long P95Ms,
    IReadOnlyList<EvalCategory> ByCategory,
    IReadOnlyList<EvalCaseResult> Cases);

/// <summary>Prices per million tokens, for the cost estimate.</summary>
public sealed record TokenPrices(decimal InputPerMillion, decimal OutputPerMillion);

public static class EvalRunner
{
    /// <summary>
    /// Engine mode: the true invoice data straight into the match + policy, no model at all.
    /// Measures the deterministic layer; anything below 100% is a bug in the rules or the labels.
    /// </summary>
    public static async Task<EvalReport> RunEngineAsync(IReadOnlyList<EvalCase> cases, CancellationToken ct = default)
    {
        var results = new List<EvalCaseResult>();
        foreach (var c in cases)
        {
            var watch = Stopwatch.StartNew();
            var erp = await FreshErpAsync(ct);
            var match = MatchEngine.Run(c.Truth, await new MatchService(erp).LoadContextAsync(c.Truth, ct), ExtractionChecks.Run(c.Truth));
            var decision = await new DecisionService(erp, Options.Create(new ApPolicyOptions())).DecideAsync(match, null, ct);
            results.Add(new(c.Id, c.Category, c.Expected, decision.Outcome, null, HasCodes(match, c), false, null, 0, 0, watch.ElapsedMilliseconds, null));
        }
        return Summarise("engine", "none", results, null);
    }

    /// <summary>
    /// Agent mode: the full pipeline as in production. The PDF is read by the model, the agent
    /// investigates with tools, and the guardrail decides. Every case gets a fresh ERP.
    /// </summary>
    public static async Task<EvalReport> RunAgentAsync(IReadOnlyList<EvalCase> cases, ILlmClient llm, TokenPrices? prices = null,
        AgentOptions? options = null, Action<EvalCaseResult>? progress = null, CancellationToken ct = default)
    {
        var results = new List<EvalCaseResult>();
        foreach (var c in cases)
        {
            var watch = Stopwatch.StartNew();
            var erp = await FreshErpAsync(ct);
            var usage = LlmUsage.None;
            EvalCaseResult result;
            try
            {
                var extraction = await new InvoiceExtractor(llm, TimeProvider.System).ExtractAsync(c.RenderPdf(), FileSniffer.Pdf, ct);
                usage = usage.Add(extraction.Usage);
                var invoice = new Invoice($"ap_{c.Id}", InvoiceStatus.Investigating, new InvoiceFile($"{c.Id}.pdf", FileSniffer.Pdf, 0, c.Id),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, extraction);
                var matcher = new MatchService(erp);
                var agent = new InvoiceAgent(llm, erp, matcher, Options.Create(new ApPolicyOptions()),
                    Options.Create(options ?? new AgentOptions()), TimeProvider.System, NullLogger<InvoiceAgent>.Instance);
                var run = await agent.RunAsync(invoice, ct);
                usage = usage.Add(run.Usage);
                var match = await matcher.MatchAsync(extraction, ct);
                var proposal = run.Status == AgentRunStatus.Completed ? run.Proposal : null;
                var decision = await new DecisionService(erp, Options.Create(new ApPolicyOptions())).DecideAsync(match, proposal, ct);
                result = new(c.Id, c.Category, c.Expected, decision.Outcome, proposal?.Outcome, HasCodes(match, c),
                    decision.Source == DecisionSource.PolicyOverride, SameFacts(extraction.Data, c.Truth), usage.PromptTokens,
                    usage.CompletionTokens, watch.ElapsedMilliseconds, run.Status == AgentRunStatus.Failed ? run.FailureReason : null);
            }
            catch (ExtractionFailedException e)
            {
                result = new(c.Id, c.Category, c.Expected, null, null, false, false, false, usage.PromptTokens, usage.CompletionTokens,
                    watch.ElapsedMilliseconds, e.Message);
            }
            results.Add(result);
            progress?.Invoke(result);
        }
        return Summarise("agent", llm.Model, results, prices);
    }

    private static async Task<IErpStore> FreshErpAsync(CancellationToken ct)
    {
        var erp = new InMemoryErpStore();
        await erp.UpsertAsync(ErpSeed.Create(), ct);
        return erp;
    }

    private static bool HasCodes(MatchReport match, EvalCase c) =>
        c.ExpectedCodes.All(code => match.Findings.Any(f => f.Code == code));

    /// <summary>The fields a decision depends on were read exactly.</summary>
    public static bool SameFacts(ExtractedInvoice read, ExtractedInvoice truth) =>
        MatchEngine.NormalizeInvoiceNumber(read.InvoiceNumber ?? "") == MatchEngine.NormalizeInvoiceNumber(truth.InvoiceNumber ?? "")
        && read.PoNumber?.Trim().ToUpperInvariant() == truth.PoNumber?.ToUpperInvariant()
        && read.Total == truth.Total
        && read.TaxTotal == truth.TaxTotal
        && read.Lines.Count == truth.Lines.Count
        && read.Lines.Zip(truth.Lines).All(p => p.First.Quantity == p.Second.Quantity && p.First.UnitPrice == p.Second.UnitPrice);

    private static EvalReport Summarise(string mode, string model, List<EvalCaseResult> results, TokenPrices? prices)
    {
        var n = Math.Max(results.Count, 1);
        var ms = results.Select(r => r.Ms).OrderBy(x => x).ToList();
        long Percentile(double p) => ms.Count == 0 ? 0 : ms[Math.Min(ms.Count - 1, (int)Math.Ceiling(p * ms.Count) - 1)];
        var agentRuns = results.Where(r => r.Proposed is not null || r.Failure is not null).ToList();
        var extractions = results.Where(r => r.ExtractionExact is not null).ToList();
        var promptTokens = results.Sum(r => (long)r.PromptTokens);
        var completionTokens = results.Sum(r => (long)r.CompletionTokens);
        var tokens = promptTokens + completionTokens;

        return new EvalReport(
            mode,
            model,
            results.Count,
            results.Count(r => r.Correct),
            Math.Round(100m * results.Count(r => r.Correct) / n, 1),
            results.Count(r => r.FalseApproval),
            results.Count(r => r.Failure is not null),
            mode == "agent" ? Math.Round(100m * results.Count(r => r.Proposed == r.Expected) / n, 1) : null,
            results.Count(r => r.Overridden),
            extractions.Count == 0 ? null : Math.Round(100m * extractions.Count(r => r.ExtractionExact == true) / extractions.Count, 1),
            tokens,
            prices is null || mode != "agent" ? null
                : Math.Round((promptTokens * prices.InputPerMillion + completionTokens * prices.OutputPerMillion) / 1_000_000m, 4),
            Percentile(0.50),
            Percentile(0.95),
            results.GroupBy(r => r.Category).Select(g => new EvalCategory(g.Key, g.Count(), g.Count(r => r.Correct))).ToList(),
            results);
    }

    public static string ToMarkdown(EvalReport r)
    {
        var c = CultureInfo.InvariantCulture;
        var md = new StringBuilder();
        md.AppendLine($"# InvoicePilot evaluation: {r.Mode} mode");
        md.AppendLine();
        md.AppendLine($"Model: `{r.Model}` · cases: {r.Count} · generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        md.AppendLine();
        md.AppendLine("| Metric | Value |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Decision accuracy (after guardrail) | {r.Accuracy.ToString(c)}% ({r.Correct}/{r.Count}) |");
        md.AppendLine($"| False approvals | {r.FalseApprovals} |");
        md.AppendLine($"| Pipeline failures | {r.Failures} |");
        if (r.AgentProposalAccuracy is { } pa) md.AppendLine($"| Agent proposal accuracy (before guardrail) | {pa.ToString(c)}% |");
        if (r.Mode == "agent") md.AppendLine($"| Guardrail overrides | {r.Overrides} |");
        if (r.ExtractionAccuracy is { } ea) md.AppendLine($"| Extraction exact on decision fields | {ea.ToString(c)}% |");
        if (r.Mode == "agent") md.AppendLine($"| Tokens (total / per invoice) | {r.TotalTokens:N0} / {(r.Count == 0 ? 0 : r.TotalTokens / r.Count):N0} |");
        if (r.EstimatedCostUsd is { } cost) md.AppendLine($"| Estimated cost (total / per invoice) | ${cost.ToString(c)} / ${(r.Count == 0 ? 0 : Math.Round(cost / r.Count, 5)).ToString(c)} |");
        md.AppendLine($"| Latency p50 / p95 | {r.P50Ms} ms / {r.P95Ms} ms |");
        md.AppendLine();
        md.AppendLine("## By category");
        md.AppendLine();
        md.AppendLine("| Category | Cases | Correct |");
        md.AppendLine("|---|---|---|");
        foreach (var g in r.ByCategory) md.AppendLine($"| {g.Category} | {g.Count} | {g.Correct} |");
        var wrong = r.Cases.Where(x => !x.Correct).ToList();
        if (wrong.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Misses");
            md.AppendLine();
            md.AppendLine("| Case | Category | Expected | Got | Agent proposed | Note |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var x in wrong)
            {
                md.AppendLine($"| {x.Id} | {x.Category} | {x.Expected} | {x.Final?.ToString() ?? "failed"} | {x.Proposed?.ToString() ?? "-"} | {(x.Failure ?? "").Replace('|', '/')} |");
            }
        }
        return md.ToString();
    }
}
