using InvoicePilot.Api.Agent;
using InvoicePilot.Api.Ai;
using InvoicePilot.Api.Evaluation;
using InvoicePilot.Api.Policy;
using Microsoft.Extensions.Options;

namespace InvoicePilot.Api.Tests.Agent;

public class DemoLlmClientTests
{
    [Fact]
    public async Task The_demo_reads_every_generated_pdf_exactly()
    {
        var demo = new DemoLlmClient(Options.Create(new ApPolicyOptions()));
        var extractor = new Api.Extraction.InvoiceExtractor(demo, TimeProvider.System);

        foreach (var c in EvalScenarios.Generate())
        {
            var read = await extractor.ExtractAsync(c.RenderPdf(), Api.Invoices.FileSniffer.Pdf);
            Assert.True(EvalRunner.SameFacts(read.Data, c.Truth), $"{c.Id} ({c.Category}) was not read exactly");
        }
    }

    [Fact]
    public async Task The_full_pipeline_with_the_demo_scores_100_percent()
    {
        var report = await EvalRunner.RunAgentAsync(EvalScenarios.Generate(), new DemoLlmClient(Options.Create(new ApPolicyOptions())));

        Assert.Equal(100m, report.Accuracy);
        Assert.Equal(0, report.FalseApprovals);
        Assert.Equal(0, report.Failures);
        Assert.Equal(0, report.Overrides);
        Assert.Equal("demo-rules (not an AI)", report.Model);
    }
}
