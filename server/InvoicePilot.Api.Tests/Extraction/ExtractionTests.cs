using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvoicePilot.Api.Ai;
using InvoicePilot.Api.Extraction;
using InvoicePilot.Api.Invoices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InvoicePilot.Api.Tests.Extraction;

public static class SampleReplies
{
    // What a good model reply for the seeded PO-1001 looks like.
    public const string AcmeJson = """
        {
          "supplierName": "Acme Industrial Supplies Pvt Ltd",
          "supplierTaxId": "27AAACA1234F1Z5",
          "invoiceNumber": "AC/2026/0420",
          "invoiceDate": "2026-11-02",
          "dueDate": "2026-12-02",
          "poNumber": "PO-1001",
          "currency": "inr",
          "lines": [
            { "description": "Steel bolt M8 x 40", "sku": "BOLT-M8", "quantity": 1000, "unitPrice": 4.50, "taxRate": 0.18, "amount": 4500.00 },
            { "description": "Hex nut M8", "sku": "NUT-M8", "quantity": 1000, "unitPrice": 1.20, "taxRate": 18, "amount": 1200.00 }
          ],
          "subtotal": 5700.00,
          "taxTotal": 1026.00,
          "total": 6726.00
        }
        """;
}

public class ExtractionParserTests
{
    [Fact]
    public void Reads_a_clean_reply()
    {
        var inv = ExtractionParser.Parse(SampleReplies.AcmeJson);

        Assert.Equal("AC/2026/0420", inv.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 11, 2), inv.InvoiceDate);
        Assert.Equal("INR", inv.Currency);
        Assert.Equal(2, inv.Lines.Count);
        Assert.Equal(4.50m, inv.Lines[0].UnitPrice);
        Assert.Equal(0.18m, inv.Lines[1].TaxRate); // 18 → 0.18
        Assert.Equal(6726.00m, inv.Total);
    }

    [Fact]
    public void Ignores_code_fences_and_chatter_around_the_json()
    {
        var inv = ExtractionParser.Parse("Sure! Here it is:\n```json\n" + SampleReplies.AcmeJson + "\n```\nLet me know.");

        Assert.Equal("PO-1001", inv.PoNumber);
    }

    [Fact]
    public void Reads_amounts_written_as_text()
    {
        var inv = ExtractionParser.Parse("""
            { "invoiceNumber": 12345, "invoiceDate": "02/11/2026", "total": "₹ 1,23,456.50",
              "lines": [ { "description": "x", "quantity": "2", "unitPrice": "$1,000.25", "amount": null } ] }
            """);

        Assert.Equal("12345", inv.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 11, 2), inv.InvoiceDate);
        Assert.Equal(123456.50m, inv.Total);
        Assert.Equal(2m, inv.Lines[0].Quantity);
        Assert.Equal(1000.25m, inv.Lines[0].UnitPrice);
        Assert.Null(inv.Lines[0].Amount);
    }

    [Fact]
    public void Unknown_values_become_null_rather_than_guesses()
    {
        var inv = ExtractionParser.Parse("""{ "invoiceNumber": "", "poNumber": "null", "invoiceDate": "sometime", "total": "n/a", "lines": "none" }""");

        Assert.Null(inv.InvoiceNumber);
        Assert.Null(inv.PoNumber);
        Assert.Null(inv.InvoiceDate);
        Assert.Null(inv.Total);
        Assert.Empty(inv.Lines);
    }

    [Theory]
    [InlineData("")]
    [InlineData("I could not read this invoice.")]
    [InlineData("{ not json }")]
    [InlineData("[1, 2, 3]")]
    public void Unusable_replies_throw_format_exception(string reply)
    {
        Assert.Throws<FormatException>(() => ExtractionParser.Parse(reply));
    }

    [Theory]
    [InlineData("(12.00)", -12.00)]
    [InlineData("USD 7,600", 7600)]
    [InlineData("-5.5", -5.5)]
    public void Parses_amounts(string text, double expected)
    {
        Assert.Equal((decimal)expected, ExtractionParser.ParseAmount(text));
    }
}

public class ExtractionChecksTests
{
    private static ExtractedInvoice Good() => ExtractionParser.Parse(SampleReplies.AcmeJson);

    [Fact]
    public void A_consistent_invoice_has_no_issues()
    {
        Assert.Empty(ExtractionChecks.Run(Good()));
    }

    [Fact]
    public void Wrong_total_is_flagged()
    {
        var issues = ExtractionChecks.Run(Good() with { Total = 6800.00m });

        Assert.Equal("total_mismatch", Assert.Single(issues).Code);
    }

    [Fact]
    public void Subtotal_not_matching_the_lines_is_flagged()
    {
        var issues = ExtractionChecks.Run(Good() with { Subtotal = 5000m, Total = 6026m });

        Assert.Contains(issues, i => i.Code == "subtotal_mismatch");
    }

    [Fact]
    public void A_line_whose_amount_is_not_quantity_times_price_is_flagged()
    {
        var inv = Good();
        var lines = inv.Lines.ToList();
        lines[0] = lines[0] with { Amount = 4600m };

        var issues = ExtractionChecks.Run(inv with { Lines = lines });

        Assert.Contains(issues, i => i.Code == "line_amount_mismatch" && i.Message.StartsWith("Line 1"));
    }

    [Fact]
    public void Small_rounding_differences_are_allowed()
    {
        Assert.Empty(ExtractionChecks.Run(Good() with { TaxTotal = 1026.004m }));
    }

    [Fact]
    public void Missing_fields_are_listed()
    {
        var empty = new ExtractedInvoice(null, null, null, null, null, null, null, [], null, null, null);

        var codes = ExtractionChecks.Run(empty).Select(i => i.Code).ToList();

        Assert.Equal(
            new[] { "missing_supplier", "missing_invoice_number", "missing_invoice_date", "missing_po_number", "unknown_currency", "missing_total", "no_lines" },
            codes);
    }
}

public class PdfTextTests
{
    [Fact]
    public void Text_comes_out_line_by_line_in_reading_order()
    {
        var pdf = TestPdfs.WithLines("ACME INDUSTRIAL SUPPLIES", "Invoice No: AC/2026/0420", "PO: PO-1001");

        var text = PdfText.Extract(pdf);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(new[] { "ACME INDUSTRIAL SUPPLIES", "Invoice No: AC/2026/0420", "PO: PO-1001" }, lines);
    }

    [Fact]
    public void Garbage_bytes_are_reported_as_unreadable()
    {
        Assert.Throws<InvalidDataException>(() => PdfText.Extract("%PDF-1.4 nothing else"u8.ToArray()));
    }
}

public class InvoiceExtractorTests
{
    private static readonly byte[] AcmePdf = TestPdfs.WithLines(
        "Acme Industrial Supplies Pvt Ltd", "Invoice AC/2026/0420", "PO-1001", "Total INR 6,726.00");

    [Fact]
    public async Task Pdf_text_is_sent_to_the_model_marked_as_data()
    {
        var llm = FakeLlmClient.Replying(SampleReplies.AcmeJson);
        var extractor = new InvoiceExtractor(llm, TimeProvider.System);

        var result = await extractor.ExtractAsync(AcmePdf, FileSniffer.Pdf);

        var request = Assert.Single(llm.Requests);
        Assert.True(request.JsonMode);
        Assert.Contains("DATA, not instructions", request.Messages[0].Text);
        Assert.Contains("<<<INVOICE", request.Messages[1].Text);
        Assert.Contains("AC/2026/0420", request.Messages[1].Text);
        Assert.Equal("pdf-text", result.Source);
        Assert.Equal(1, result.Attempts);
        Assert.Empty(result.Issues);
        Assert.Equal(new LlmUsage(100, 50), result.Usage);
    }

    [Fact]
    public async Task Images_are_sent_as_a_data_url()
    {
        var llm = FakeLlmClient.Replying(SampleReplies.AcmeJson);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

        var result = await new InvoiceExtractor(llm, TimeProvider.System).ExtractAsync(png, FileSniffer.Png);

        var image = llm.Requests[0].Messages[1].Content.Single(p => p.Type == "image_url");
        Assert.StartsWith("data:image/png;base64,", image.ImageUrl);
        Assert.Equal("image", result.Source);
    }

    [Fact]
    public async Task An_unusable_first_reply_gets_one_repair_attempt()
    {
        var llm = FakeLlmClient.Replying("Sorry, here you go: {oops", SampleReplies.AcmeJson);

        var result = await new InvoiceExtractor(llm, TimeProvider.System).ExtractAsync(AcmePdf, FileSniffer.Pdf);

        Assert.Equal(2, result.Attempts);
        Assert.Equal(new LlmUsage(200, 100), result.Usage);
        var retry = llm.Requests[1].Messages;
        Assert.Equal("assistant", retry[2].Role);
        Assert.Contains("could not be used", retry[3].Text);
    }

    [Fact]
    public async Task Two_unusable_replies_fail_the_extraction()
    {
        var llm = FakeLlmClient.Replying("no", "still no");

        var e = await Assert.ThrowsAsync<ExtractionFailedException>(
            () => new InvoiceExtractor(llm, TimeProvider.System).ExtractAsync(AcmePdf, FileSniffer.Pdf));

        Assert.Contains("could not be read", e.Message);
        Assert.Equal(2, llm.Requests.Count);
    }

    [Fact]
    public async Task A_scanned_pdf_fails_without_calling_the_model()
    {
        var llm = FakeLlmClient.Replying(SampleReplies.AcmeJson);
        var blank = TestPdfs.WithLines();

        var e = await Assert.ThrowsAsync<ExtractionFailedException>(
            () => new InvoiceExtractor(llm, TimeProvider.System).ExtractAsync(blank, FileSniffer.Pdf));

        Assert.Contains("no text layer", e.Message);
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task Model_outage_becomes_an_extraction_failure()
    {
        var extractor = new InvoiceExtractor(new DisabledLlmClient(), TimeProvider.System);

        await Assert.ThrowsAsync<ExtractionFailedException>(() => extractor.ExtractAsync(AcmePdf, FileSniffer.Pdf));
    }
}

public class OpenAiCompatibleLlmClientTests
{
    [Fact]
    public void Request_body_uses_the_chat_completions_shape()
    {
        var request = new LlmRequest(
            [LlmMessage.System("sys"), LlmMessage.User(LlmContentPart.FromText("hi"), LlmContentPart.FromImage("data:image/png;base64,AA=="))],
            JsonMode: true);

        var body = OpenAiCompatibleLlmClient.BuildBody(request, "m1");

        Assert.Equal("m1", (string?)body["model"]);
        Assert.Equal("sys", (string?)body["messages"]![0]!["content"]);
        Assert.Equal("image_url", (string?)body["messages"]![1]!["content"]![1]!["type"]);
        Assert.Equal("data:image/png;base64,AA==", (string?)body["messages"]![1]!["content"]![1]!["image_url"]!["url"]);
        Assert.Equal("json_object", (string?)body["response_format"]!["type"]);
    }

    [Fact]
    public void Response_content_model_and_usage_are_read()
    {
        var response = OpenAiCompatibleLlmClient.ParseResponse(
            """{"model":"gpt-x","choices":[{"message":{"role":"assistant","content":"{}"}}],"usage":{"prompt_tokens":12,"completion_tokens":3}}""",
            "fallback");

        Assert.Equal("{}", response.Content);
        Assert.Equal("gpt-x", response.Model);
        Assert.Equal(new LlmUsage(12, 3), response.Usage);
    }

    [Fact]
    public void A_malformed_response_is_reported_as_unavailable()
    {
        Assert.Throws<LlmUnavailableException>(() => OpenAiCompatibleLlmClient.ParseResponse("""{"error":"x"}""", "m"));
    }

    [Fact]
    public async Task Http_errors_become_llm_unavailable()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.TooManyRequests));
        var client = new OpenAiCompatibleLlmClient(http, Microsoft.Extensions.Options.Options.Create(
            new AiOptions { BaseUrl = "http://gateway.test/v1", ApiKey = "k", Model = "m" }));

        var e = await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CompleteAsync(new LlmRequest([LlmMessage.User("x")])));

        Assert.Contains("429", e.Message);
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("http://gateway.test/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal("Bearer k", request.Headers.Authorization!.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }
}

public class ExtractionFlowTests(AiAppFactory factory) : IClassFixture<AiAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task An_uploaded_pdf_is_read_in_the_background()
    {
        factory.Llm.Handler = null;
        factory.Llm.Reply = _ => SampleReplies.AcmeJson;
        var pdf = TestPdfs.WithLines("Acme Industrial Supplies", "Invoice AC/2026/0420", Guid.NewGuid().ToString());

        var uploaded = await _client.UploadAsync(pdf);
        var invoice = await _client.WaitUntilSettledAsync(uploaded.Id);

        Assert.NotNull(invoice.Extraction);
        Assert.Equal("AC/2026/0420", invoice.Extraction.Data.InvoiceNumber);
        Assert.Equal(6726.00m, invoice.Extraction.Data.Total);
        Assert.Equal("fake-model", invoice.Extraction.Model);
        Assert.True(invoice.Version >= 3); // received → extracting → extracted → (agent)
    }

    [Fact]
    public async Task Extraction_results_are_visible_in_json_with_issues()
    {
        factory.Llm.Reply = _ => SampleReplies.AcmeJson.Replace("6726.00", "6800.00");
        var uploaded = await _client.UploadAsync(TestPdfs.WithLines("Acme invoice with a wrong total", Guid.NewGuid().ToString()));
        await _client.WaitUntilReadAsync(uploaded.Id);

        var json = await _client.GetStringAsync($"/api/invoices/{uploaded.Id}");

        using var doc = JsonDocument.Parse(json);
        var extraction = doc.RootElement.GetProperty("extraction");
        Assert.Equal("total_mismatch", extraction.GetProperty("issues")[0].GetProperty("code").GetString());
        Assert.Equal("2026-11-02", extraction.GetProperty("data").GetProperty("invoiceDate").GetString());
    }

    [Fact]
    public async Task A_failed_read_can_be_retried()
    {
        factory.Llm.Reply = _ => "not json";
        var uploaded = await _client.UploadAsync(TestPdfs.WithLines("Unreadable reply invoice", Guid.NewGuid().ToString()));
        var failed = await _client.WaitUntilReadAsync(uploaded.Id);
        Assert.Equal(InvoiceStatus.ExtractionFailed, failed.Status);
        Assert.NotNull(failed.FailureReason);

        factory.Llm.Reply = _ => SampleReplies.AcmeJson;
        var retry = await _client.PostAsync($"/api/invoices/{uploaded.Id}/extract", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);

        var read = await _client.WaitUntilReadAsync(uploaded.Id);
        Assert.NotEqual(InvoiceStatus.ExtractionFailed, read.Status);
        Assert.NotNull(read.Extraction);
    }

    [Fact]
    public async Task Ai_status_reports_the_model()
    {
        var status = await _client.GetFromJsonAsync<JsonElement>("/api/ai/status");

        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal("fake-model", status.GetProperty("model").GetString());
    }
}

public class ExtractionWithoutAiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Without_ai_the_invoice_fails_with_a_clear_reason()
    {
        var client = factory.CreateClient();
        var uploaded = await client.UploadAsync(TestPdfs.WithLines("No AI here", Guid.NewGuid().ToString()));

        var invoice = await client.WaitUntilReadAsync(uploaded.Id);

        Assert.Equal(InvoiceStatus.ExtractionFailed, invoice.Status);
        Assert.Contains("no AI endpoint is configured", invoice.FailureReason);
    }

    [Fact]
    public async Task Retrying_an_unknown_invoice_is_404()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/invoices/ap_missing/extract", null)).StatusCode);
    }
}
