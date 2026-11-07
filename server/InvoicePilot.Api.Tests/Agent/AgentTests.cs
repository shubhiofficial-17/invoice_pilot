using System.Text.Json;
using InvoicePilot.Api.Agent;
using InvoicePilot.Api.Ai;
using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Extraction;
using InvoicePilot.Api.Invoices;
using InvoicePilot.Api.Matching;
using InvoicePilot.Api.Policy;
using InvoicePilot.Api.Tests.Extraction;
using InvoicePilot.Api.Tests.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static InvoicePilot.Api.Tests.AgentScript;

namespace InvoicePilot.Api.Tests.Agent;

public class InvoiceAgentTests
{
    private static async Task<IErpStore> SeededErpAsync()
    {
        var erp = new InMemoryErpStore();
        await erp.UpsertAsync(ErpSeed.Create());
        return erp;
    }

    public static Invoice ReadInvoice(ExtractedInvoice data) => new(
        "ap_test", InvoiceStatus.Investigating, new InvoiceFile("i.pdf", FileSniffer.Pdf, 1, "x"),
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 3,
        new ExtractionResult(data, ExtractionChecks.Run(data), "pdf-text", "fake-model", 1, LlmUsage.None, DateTimeOffset.UnixEpoch));

    private static async Task<(AgentRun Run, FakeLlmClient Llm)> RunAsync(
        Func<LlmRequest, LlmResponse> handler, ExtractedInvoice? data = null, AgentOptions? options = null)
    {
        var llm = new FakeLlmClient { Handler = handler };
        var erp = await SeededErpAsync();
        var agent = new InvoiceAgent(llm, erp, new MatchService(erp), Options.Create(new ApPolicyOptions()),
            Options.Create(options ?? new AgentOptions { RetryDelaysMs = [0, 0] }), TimeProvider.System,
            NullLogger<InvoiceAgent>.Instance);
        var run = await agent.RunAsync(ReadInvoice(data ?? MatchEngineTests.Billing("PO-1001")));
        return (run, llm);
    }

    [Fact]
    public async Task A_standard_run_reads_matches_checks_policy_and_decides()
    {
        var (run, llm) = await RunAsync(Steps("", Standard("approve")));

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(Outcome.Approve, run.Proposal!.Outcome);
        Assert.Equal(4, run.ModelCalls);
        Assert.Equal(new[] { "get_invoice", "run_three_way_match", "read_ap_policy", "submit_decision" },
            run.Steps.Where(s => s.Kind == StepKind.Tool).Select(s => s.ToolName));
        Assert.All(run.Steps.Where(s => s.Kind == StepKind.Tool), s => Assert.False(s.IsError, s.Output));
        Assert.Equal(new LlmUsage(800, 80), run.Usage);
        Assert.Contains("\"supplierId\":\"SUP-ACME\"", run.Steps.Single(s => s.ToolName == "run_three_way_match").Output);
    }

    [Fact]
    public async Task The_model_gets_the_tools_and_is_told_the_invoice_is_untrusted()
    {
        var (_, llm) = await RunAsync(Steps("", Standard("approve")));

        var first = llm.Requests[0];
        Assert.Equal(8, first.Tools!.Count);
        Assert.Contains(first.Tools, t => t.Name == "submit_decision");
        Assert.Contains("untrusted data", first.Messages[0].Text);
        // Tool results go back as tool messages answering the call id.
        var second = llm.Requests[1];
        Assert.Equal("call_0", second.Messages.Single(m => m.Role == "tool").ToolCallId);
    }

    [Fact]
    public async Task Deciding_before_matching_is_refused_and_the_model_recovers()
    {
        var (run, _) = await RunAsync(Steps("", Submit("approve"), Call("run_three_way_match"), Submit("approve")));

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        var refused = run.Steps.First(s => s.ToolName == "submit_decision");
        Assert.True(refused.IsError);
        Assert.Contains("run_three_way_match", refused.Output);
    }

    [Fact]
    public async Task Unknown_tools_and_bad_arguments_are_reported_back_to_the_model()
    {
        var (run, _) = await RunAsync(Steps("",
            Call("pay_invoice", """{"amount": 1}"""),
            Call("get_purchase_order", "{not json"),
            Call("get_purchase_order", """{"po_number": ""}"""),
            Call("run_three_way_match"),
            Submit("approve")));

        var tools = run.Steps.Where(s => s.Kind == StepKind.Tool).ToList();
        Assert.Contains("Unknown tool", tools[0].Output);
        Assert.Contains("not valid JSON", tools[1].Output);
        Assert.Contains("is required", tools[2].Output);
        Assert.True(tools.Take(3).All(t => t.IsError));
        Assert.Equal(AgentRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task Finding_codes_must_come_from_the_match_report()
    {
        var (run, _) = await RunAsync(Steps("",
            Call("run_three_way_match"),
            Submit("approve", codes: """["everything_fine"]"""),
            Submit("approve")));

        Assert.Contains("not in the match report", run.Steps.First(s => s.ToolName == "submit_decision").Output);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task A_hold_needs_an_email_and_the_recipient_comes_from_the_erp()
    {
        var email = """{"subject":"Invoice INV-T-1: quantity","body":"You billed 500 boxes of gloves but we received 300. Please send a corrected invoice."}""";
        var data = MatchEngineTests.Billing("PO-1005");

        var (run, _) = await RunAsync(Steps("",
            Call("run_three_way_match"),
            Submit("hold", codes: """["quantity_exceeds_received"]"""),
            Submit("hold", codes: """["quantity_exceeds_received"]""", emailJson: email)), data);

        Assert.Contains("needs supplier_email", run.Steps.First(s => s.ToolName == "submit_decision").Output);
        Assert.Equal(Outcome.Hold, run.Proposal!.Outcome);
        Assert.Equal("ar@acme-industrial.example", run.Proposal.SupplierEmail!.To);
        Assert.Equal(new[] { "quantity_exceeds_received" }, run.Proposal.FindingCodes);
    }

    [Fact]
    public async Task A_model_that_stops_talking_gets_one_nudge_then_fails()
    {
        var (run, llm) = await RunAsync(_ => new LlmResponse("Looks fine to me.", "fake-model", new LlmUsage(10, 5)));

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains("without submitting", run.FailureReason);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains("You have not submitted", llm.Requests[1].Messages[^1].Text);
    }

    [Fact]
    public async Task Repeating_the_same_call_is_stopped_and_the_run_ends_at_the_step_limit()
    {
        var (run, _) = await RunAsync(
            _ => new LlmResponse("", "fake-model", new LlmUsage(10, 5), [new LlmToolCall("c", "get_invoice", "{}")]),
            options: new AgentOptions { MaxModelCalls = 5, RetryDelaysMs = [0] });

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains("No decision after 5", run.FailureReason);
        Assert.Contains("already made this exact call", run.Steps.Last().Output);
    }

    [Fact]
    public async Task The_token_budget_stops_a_runaway_run()
    {
        var (run, _) = await RunAsync(
            _ => new LlmResponse("", "fake-model", new LlmUsage(5_000, 100), [new LlmToolCall("c", "get_invoice", "{}")]),
            options: new AgentOptions { TokenBudget = 12_000, RetryDelaysMs = [0] });

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains("budget", run.FailureReason);
        Assert.Equal(3, run.ModelCalls);
    }

    [Fact]
    public async Task Model_outages_are_retried_then_fail_the_run()
    {
        var llm = new FakeLlmClient { Handler = Steps("", Standard("approve")), FailNext = 2 };
        var erp = await SeededErpAsync();
        var agent = new InvoiceAgent(llm, erp, new MatchService(erp), Options.Create(new ApPolicyOptions()),
            Options.Create(new AgentOptions { RetryDelaysMs = [0, 0] }), TimeProvider.System, NullLogger<InvoiceAgent>.Instance);

        var recovered = await agent.RunAsync(ReadInvoice(MatchEngineTests.Billing("PO-1001")));
        llm.FailNext = 3;
        var failed = await agent.RunAsync(ReadInvoice(MatchEngineTests.Billing("PO-1001")));

        Assert.Equal(AgentRunStatus.Completed, recovered.Status);
        Assert.Equal(AgentRunStatus.Failed, failed.Status);
        Assert.Contains("unavailable", failed.FailureReason);
    }

    [Fact]
    public async Task Tools_return_erp_data()
    {
        var (run, _) = await RunAsync(Steps("",
            Call("lookup_supplier", """{"query":"Acme Industrial Supplies Private Limited"}"""),
            Call("get_purchase_order", """{"po_number":"po-1005"}"""),
            Call("get_goods_receipts", """{"po_number":"PO-1005"}"""),
            Call("get_supplier_invoices", """{"supplier_id":"SUP-NORTH"}"""),
            Call("get_purchase_order", """{"po_number":"PO-0000"}"""),
            Call("run_three_way_match"),
            Submit("approve")), options: new AgentOptions { MaxModelCalls = 12, RetryDelaysMs = [0] });

        var outputs = run.Steps.Where(s => s.Kind == StepKind.Tool).Select(s => s.Output).ToList();
        Assert.Contains("SUP-ACME", outputs[0]);
        Assert.Contains("GLOVE-NIT", outputs[1]);
        Assert.Contains("GRN-5005", outputs[2]);
        Assert.Contains("NW-55012", outputs[3]);
        Assert.Contains("\"found\":false", outputs[4]);
    }
}

public class AgentPipelineTests(AiAppFactory factory) : IClassFixture<AiAppFactory>
{
    [Fact]
    public async Task An_uploaded_invoice_is_read_then_investigated_and_waits_for_review()
    {
        factory.Llm.Handler = Steps(SampleReplies.AcmeJson, Standard("approve"));
        var client = factory.CreateClient();

        var uploaded = await client.UploadAsync(TestPdfs.WithLines("Acme invoice", Guid.NewGuid().ToString()));
        var invoice = await client.WaitUntilSettledAsync(uploaded.Id);

        Assert.Equal(InvoiceStatus.AwaitingReview, invoice.Status);
        Assert.Equal(Outcome.Approve, invoice.Agent!.Proposal!.Outcome);
        Assert.Equal(8, invoice.Agent.Steps.Count);
    }

    [Fact]
    public async Task A_failed_investigation_can_be_run_again()
    {
        factory.Llm.Handler = r => r.Tools is null
            ? new LlmResponse(SampleReplies.AcmeJson, "fake-model", new LlmUsage(1, 1))
            : new LlmResponse("no tools for me", "fake-model", new LlmUsage(1, 1));
        var client = factory.CreateClient();
        var uploaded = await client.UploadAsync(TestPdfs.WithLines("Acme invoice again", Guid.NewGuid().ToString()));
        var failed = await client.WaitUntilSettledAsync(uploaded.Id);
        Assert.Equal(InvoiceStatus.InvestigationFailed, failed.Status);

        factory.Llm.Handler = Steps(SampleReplies.AcmeJson, Standard("approve"));
        var again = await client.PostAsync($"/api/invoices/{uploaded.Id}/investigate", null);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, again.StatusCode);

        var done = await client.WaitUntilSettledAsync(uploaded.Id);
        Assert.Equal(InvoiceStatus.AwaitingReview, done.Status);
    }

    [Fact]
    public async Task The_policy_is_published_as_text()
    {
        var text = await factory.CreateClient().GetStringAsync("/api/policy");

        Assert.Contains("more than 2%", text);
        Assert.Contains("INR 500,000", text);
    }
}

public class LlmToolCallingTests
{
    [Fact]
    public void Tools_tool_calls_and_tool_results_use_the_openai_shape()
    {
        var tool = new LlmTool("get_invoice", "d", JsonNodeObject("""{"type":"object","properties":{}}"""));
        var request = new LlmRequest(
        [
            LlmMessage.User("go"),
            LlmMessage.AssistantCalls(null, [new LlmToolCall("call_1", "get_invoice", "{}")]),
            LlmMessage.ToolResult("call_1", """{"ok":true}"""),
        ], Tools: [tool]);

        var body = OpenAiCompatibleLlmClient.BuildBody(request, "m");
        var again = OpenAiCompatibleLlmClient.BuildBody(request, "m"); // reusing the same tool schema must not throw

        Assert.Equal("get_invoice", (string?)body["tools"]![0]!["function"]!["name"]);
        Assert.Equal("auto", (string?)body["tool_choice"]);
        Assert.Null(body["messages"]![1]!["content"]);
        Assert.Equal("call_1", (string?)body["messages"]![1]!["tool_calls"]![0]!["id"]);
        Assert.Equal("tool", (string?)body["messages"]![2]!["role"]);
        Assert.Equal("call_1", (string?)body["messages"]![2]!["tool_call_id"]);
        Assert.NotNull(again);
    }

    [Fact]
    public void Tool_calls_in_a_response_are_parsed()
    {
        var response = OpenAiCompatibleLlmClient.ParseResponse("""
            {"choices":[{"message":{"role":"assistant","content":null,
              "tool_calls":[{"id":"call_9","type":"function","function":{"name":"get_purchase_order","arguments":"{\"po_number\":\"PO-1001\"}"}}]}}]}
            """, "m");

        var call = Assert.Single(response.Calls);
        Assert.Equal("call_9", call.Id);
        Assert.Equal("get_purchase_order", call.Name);
        Assert.Equal("PO-1001", JsonDocument.Parse(call.Arguments).RootElement.GetProperty("po_number").GetString());
        Assert.Equal("", response.Content);
    }

    private static System.Text.Json.Nodes.JsonObject JsonNodeObject(string json) => System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
}
