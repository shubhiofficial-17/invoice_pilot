using System.Collections.Concurrent;
using System.Net.Http.Json;
using InvoicePilot.Api.Ai;
using InvoicePilot.Api.Invoices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace InvoicePilot.Api.Tests;

/// <summary>A scripted model: each call runs the handler; every request is recorded.</summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly ConcurrentQueue<LlmRequest> _requests = new();

    public Func<LlmRequest, string> Reply { get; set; } = _ => "{}";

    /// <summary>When set, decides the whole response (tool calls, usage); otherwise Reply is used.</summary>
    public Func<LlmRequest, LlmResponse>? Handler { get; set; }

    /// <summary>When set, the next calls throw LlmUnavailableException this many times.</summary>
    public int FailNext { get; set; }

    public bool Enabled { get; set; } = true;

    public string Model => "fake-model";

    public IReadOnlyList<LlmRequest> Requests => _requests.ToList();

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        _requests.Enqueue(request);
        if (FailNext > 0)
        {
            FailNext--;
            throw new LlmUnavailableException("The AI service returned 503.");
        }
        return Task.FromResult(Handler?.Invoke(request) ?? new LlmResponse(Reply(request), Model, new LlmUsage(100, 50)));
    }

    /// <summary>Replies with each string in turn (the last one repeats).</summary>
    public static FakeLlmClient Replying(params string[] replies)
    {
        var i = 0;
        return new FakeLlmClient { Reply = _ => replies[Math.Min(Interlocked.Increment(ref i) - 1, replies.Length - 1)] };
    }
}

/// <summary>
/// Plays an agent: on each call it makes the next scripted tool call, counting the tool results
/// already in the conversation. Extraction requests (no tools) get the extraction reply.
/// </summary>
public static class AgentScript
{
    public static Func<LlmRequest, LlmResponse> Steps(string extractionReply, params (string Tool, string Args)[] steps) => request =>
    {
        if (request.Tools is null) return new LlmResponse(extractionReply, "fake-model", new LlmUsage(100, 50));
        var done = request.Messages.Count(m => m.Role == "tool");
        if (done >= steps.Length) return new LlmResponse("I am done.", "fake-model", new LlmUsage(10, 5));
        var (tool, args) = steps[done];
        return new LlmResponse("", "fake-model", new LlmUsage(200, 20), [new LlmToolCall($"call_{done}", tool, args)]);
    };

    public static (string, string) Call(string tool, string args = "{}") => (tool, args);

    public static (string, string) Submit(string outcome, string reason = "Matches the PO and receipts.", string codes = "[]", string? emailJson = null) =>
        ("submit_decision",
            "{\"outcome\":\"" + outcome + "\",\"reason\":\"" + reason + "\",\"finding_codes\":" + codes
            + (emailJson is null ? "" : ",\"supplier_email\":" + emailJson) + "}");

    /// <summary>The usual good run: read, match, read the policy, decide.</summary>
    public static (string, string)[] Standard(string outcome, string codes = "[]", string? emailJson = null) =>
    [
        Call("get_invoice"), Call("run_three_way_match"), Call("read_ap_policy"), Submit(outcome, codes: codes, emailJson: emailJson),
    ];
}

/// <summary>The API with a fake model plugged in.</summary>
public sealed class AiAppFactory : WebApplicationFactory<Program>
{
    public FakeLlmClient Llm { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services => services.AddSingleton<ILlmClient>(Llm));
    }
}

public static class TestPdfs
{
    /// <summary>A real one-page PDF with a text layer, one string per line.</summary>
    public static byte[] WithLines(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var y = 800.0;
        foreach (var line in lines)
        {
            page.AddText(line, 11, new PdfPoint(50, y), font);
            y -= 18;
        }
        return builder.Build();
    }
}

public static class InvoiceTestClient
{
    public static async Task<Invoice> UploadAsync(this HttpClient client, byte[] content, string fileName = "invoice.pdf")
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName } };
        var response = await client.PostAsync("/api/invoices", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Invoice>(TestJson.Options))!;
    }

    /// <summary>Polls until the invoice is no longer waiting for or being read.</summary>
    public static Task<Invoice> WaitUntilReadAsync(this HttpClient client, string id, int timeoutMs = 10_000) =>
        client.WaitUntilAsync(id, i => i.Status is not (InvoiceStatus.Received or InvoiceStatus.Extracting), timeoutMs);

    /// <summary>Polls until the whole pipeline (reading and the agent) has finished.</summary>
    public static Task<Invoice> WaitUntilSettledAsync(this HttpClient client, string id, int timeoutMs = 10_000) =>
        client.WaitUntilAsync(id, i => i.Status is not (InvoiceStatus.Received or InvoiceStatus.Extracting
            or InvoiceStatus.Extracted or InvoiceStatus.Investigating), timeoutMs);

    public static async Task<Invoice> WaitUntilAsync(this HttpClient client, string id, Func<Invoice, bool> done, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var invoice = (await client.GetFromJsonAsync<Invoice>($"/api/invoices/{id}", TestJson.Options))!;
            if (done(invoice)) return invoice;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Invoice {id} is still {invoice.Status}.");
            await Task.Delay(50);
        }
    }
}
