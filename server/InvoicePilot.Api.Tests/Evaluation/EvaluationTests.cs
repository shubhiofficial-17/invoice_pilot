using System.Text.Json;
using InvoicePilot.Api.Agent;
using InvoicePilot.Api.Ai;
using InvoicePilot.Api.Evaluation;
using InvoicePilot.Api.Extraction;

namespace InvoicePilot.Api.Tests.Evaluation;

public class EvalScenarioTests
{
    [Fact]
    public void The_set_has_100_labelled_cases_and_is_deterministic()
    {
        var a = EvalScenarios.Generate();
        var b = EvalScenarios.Generate();

        Assert.Equal(100, a.Count);
        Assert.Equal(100, a.Select(c => c.Id).Distinct().Count());
        Assert.Equal(a.Select(c => (c.Id, c.Category, c.Expected, c.Truth.Total)), b.Select(c => (c.Id, c.Category, c.Expected, c.Truth.Total)));
        Assert.Equal(15, a.Select(c => c.Category).Distinct().Count());
        Assert.True(a.Count(c => c.Expected != Outcome.Approve) >= 60, "Most cases should be exceptions.");
    }

    [Fact]
    public void Each_case_renders_to_a_pdf_whose_text_carries_the_facts()
    {
        var c = EvalScenarios.Generate().First(x => x.Category == "prompt_injection");

        var text = PdfText.Extract(c.RenderPdf());

        Assert.Contains($"Invoice No: {c.Truth.InvoiceNumber}", text);
        Assert.Contains("Purchase Order: PO-1005", text);
        Assert.Contains(c.ExtraText[0][..20], text);
    }
}

public class EvalRunnerTests
{
    private static readonly IReadOnlyList<EvalCase> Cases = EvalScenarios.Generate();

    private static string Wire(Outcome o) => o switch
    {
        Outcome.Approve => "approve",
        Outcome.RouteForApproval => "route_for_approval",
        Outcome.Hold => "hold",
        _ => "reject",
    };

    /// <summary>
    /// A fake model that reads each PDF perfectly (returns the true data) and, as the agent,
    /// proposes whatever <paramref name="propose"/> says for that case.
    /// </summary>
    private static FakeLlmClient Model(Func<EvalCase, Outcome> propose)
    {
        var byNumber = Cases.GroupBy(c => c.Truth.InvoiceNumber!).ToDictionary(g => g.Key, g => g.First());
        var byId = Cases.ToDictionary(c => c.Id);
        return new FakeLlmClient
        {
            Handler = request =>
            {
                if (request.Tools is null)
                {
                    var text = request.Messages[1].Text;
                    var c = byNumber.First(kv => text.Contains($"Invoice No: {kv.Key}\n") || text.Contains($"Invoice No: {kv.Key}\r")).Value;
                    return new LlmResponse(JsonSerializer.Serialize(c.Truth, TestJson.Options), "fake-model", new LlmUsage(1000, 300));
                }
                var id = request.Messages[1].Text.Split("ap_")[1].Split('.')[0];
                var outcome = propose(byId[id]);
                var email = outcome == Outcome.Hold ? """{"subject":"Invoice on hold","body":"Please send a corrected invoice for the items listed."}""" : null;
                return AgentScript.Steps("", AgentScript.Standard(Wire(outcome), emailJson: email))(request);
            },
        };
    }

    [Fact]
    public async Task Engine_mode_gets_every_case_right()
    {
        var report = await EvalRunner.RunEngineAsync(Cases);

        var misses = string.Join("; ", report.Cases.Where(c => !c.Correct).Select(c => $"{c.Id} {c.Category}: {c.Final}"));
        Assert.True(report.Correct == 100, misses);
        Assert.Equal(0, report.FalseApprovals);
        Assert.All(report.Cases, c => Assert.True(c.CodesFound, $"{c.Id} is missing its expected finding codes"));
    }

    [Fact]
    public async Task A_perfect_model_scores_100_with_no_overrides()
    {
        var report = await EvalRunner.RunAgentAsync(Cases, Model(c => c.Expected), new TokenPrices(0.15m, 0.60m),
            new AgentOptions { RetryDelaysMs = [0] });

        Assert.Equal(100m, report.Accuracy);
        Assert.Equal(100m, report.AgentProposalAccuracy);
        Assert.Equal(100m, report.ExtractionAccuracy);
        Assert.Equal(0, report.Overrides);
        Assert.Equal(0, report.Failures);
        Assert.True(report.EstimatedCostUsd > 0);
    }

    [Fact]
    public async Task An_agent_that_always_approves_still_causes_no_false_approvals()
    {
        var report = await EvalRunner.RunAgentAsync(Cases, Model(_ => Outcome.Approve), options: new AgentOptions { RetryDelaysMs = [0] });

        Assert.Equal(0, report.FalseApprovals);
        Assert.Equal(100m, report.Accuracy); // the guardrail corrects every unsafe proposal
        Assert.Equal(Cases.Count(c => c.Expected != Outcome.Approve), report.Overrides);
        Assert.Equal(Math.Round(100m * Cases.Count(c => c.Expected == Outcome.Approve) / 100, 1), report.AgentProposalAccuracy);
    }

    [Fact]
    public async Task An_over_cautious_agent_is_not_corrected_and_the_report_shows_it()
    {
        var report = await EvalRunner.RunAgentAsync(Cases, Model(_ => Outcome.Reject), options: new AgentOptions { RetryDelaysMs = [0] });

        Assert.Equal(0, report.FalseApprovals);
        Assert.Equal(Cases.Count(c => c.Expected == Outcome.Reject), report.Correct);
        Assert.Contains("## Misses", EvalRunner.ToMarkdown(report));
    }

    [Fact]
    public void The_markdown_report_has_the_headline_metrics()
    {
        var report = EvalRunner.RunEngineAsync(Cases.Take(5).ToList()).GetAwaiter().GetResult();

        var md = EvalRunner.ToMarkdown(report);

        Assert.Contains("| Decision accuracy (after guardrail) | 100% (5/5) |", md);
        Assert.Contains("| False approvals | 0 |", md);
    }
}
