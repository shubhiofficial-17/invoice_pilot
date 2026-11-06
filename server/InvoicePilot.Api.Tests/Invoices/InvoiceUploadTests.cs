using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using InvoicePilot.Api.Invoices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InvoicePilot.Api.Tests.Invoices;

public class InvoiceUploadTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    public static byte[] FakePdf(string text) => Encoding.ASCII.GetBytes("%PDF-1.4\n% " + text + "\n%%EOF\n");

    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    private Task<HttpResponseMessage> UploadAsync(byte[] content, string fileName, string contentType = "application/pdf")
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, "file", fileName);
        return _client.PostAsync("/api/invoices", form);
    }

    [Fact]
    public async Task Uploading_a_pdf_creates_a_received_invoice()
    {
        var bytes = FakePdf(Guid.NewGuid().ToString());

        var response = await UploadAsync(bytes, "acme-0420.pdf");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invoice = await response.Content.ReadFromJsonAsync<Invoice>(TestJson.Options);
        Assert.NotNull(invoice);
        Assert.StartsWith("ap_", invoice.Id);
        Assert.Equal(InvoiceStatus.Received, invoice.Status);
        Assert.Equal("acme-0420.pdf", invoice.File.FileName);
        Assert.Equal(FileSniffer.Pdf, invoice.File.ContentType);
        Assert.Equal(bytes.Length, invoice.File.Size);
        Assert.Equal(64, invoice.File.Sha256.Length);
        Assert.Equal($"/api/invoices/{invoice.Id}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task The_file_can_be_downloaded_unchanged()
    {
        var bytes = FakePdf(Guid.NewGuid().ToString());
        var invoice = await (await UploadAsync(bytes, "a.pdf")).Content.ReadFromJsonAsync<Invoice>(TestJson.Options);

        var response = await _client.GetAsync($"/api/invoices/{invoice!.Id}/file");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
    }

    [Fact]
    public async Task Uploading_the_same_file_again_returns_the_existing_invoice()
    {
        var bytes = FakePdf(Guid.NewGuid().ToString());
        var first = await (await UploadAsync(bytes, "first.pdf")).Content.ReadFromJsonAsync<Invoice>(TestJson.Options);

        var again = await UploadAsync(bytes, "renamed.pdf");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var second = await again.Content.ReadFromJsonAsync<Invoice>(TestJson.Options);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal("first.pdf", second.File.FileName);
    }

    [Fact]
    public async Task File_type_comes_from_the_bytes_not_the_header()
    {
        var png = PngHeader.Concat(Guid.NewGuid().ToByteArray()).ToArray();

        var response = await UploadAsync(png, "scan.pdf", contentType: "application/pdf");

        var invoice = await response.Content.ReadFromJsonAsync<Invoice>(TestJson.Options);
        Assert.Equal(FileSniffer.Png, invoice!.File.ContentType);
    }

    [Fact]
    public async Task Other_file_types_are_rejected_with_415()
    {
        var response = await UploadAsync(Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "invoice.pdf");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Missing_file_is_a_validation_error()
    {
        var response = await _client.PostAsync("/api/invoices", new MultipartFormDataContent { { new StringContent("x"), "other" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("file", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Files_over_10_mb_are_rejected()
    {
        var big = new byte[InvoiceLimits.MaxFileBytes + 1];
        "%PDF-"u8.CopyTo(big);

        var response = await UploadAsync(big, "big.pdf");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("10 MB", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Uploaded_invoices_are_listed_and_fetchable()
    {
        var invoice = await (await UploadAsync(FakePdf(Guid.NewGuid().ToString()), "listed.pdf"))
            .Content.ReadFromJsonAsync<Invoice>(TestJson.Options);

        var list = await _client.GetFromJsonAsync<List<Invoice>>("/api/invoices", TestJson.Options);
        var one = await _client.GetFromJsonAsync<Invoice>($"/api/invoices/{invoice!.Id}", TestJson.Options);

        Assert.Contains(list!, i => i.Id == invoice.Id);
        Assert.Equal(invoice, one);
    }

    [Theory]
    [InlineData("/api/invoices/ap_missing")]
    [InlineData("/api/invoices/ap_missing/file")]
    public async Task Unknown_invoice_returns_404(string url)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(url)).StatusCode);
    }
}

public class FileSnifferTests
{
    [Theory]
    [InlineData("../../etc/passwd.pdf", "passwd.pdf")]
    [InlineData("C:\\Users\\me\\inv.pdf", "inv.pdf")]
    [InlineData("in\"voice\u0001.pdf", "invoice.pdf")]
    [InlineData("", "invoice.pdf")]
    [InlineData("...", "invoice.pdf")]
    public void File_names_are_made_safe(string input, string expected)
    {
        Assert.Equal(expected, FileSniffer.SafeFileName(input, FileSniffer.Pdf));
    }

    [Fact]
    public void Long_names_keep_their_extension()
    {
        var name = new string('a', 300) + ".pdf";

        var safe = FileSniffer.SafeFileName(name, FileSniffer.Pdf);

        Assert.Equal(120, safe.Length);
        Assert.EndsWith(".pdf", safe);
    }

    [Fact]
    public void Jpeg_is_detected()
    {
        Assert.Equal(FileSniffer.Jpeg, FileSniffer.Detect(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.Null(FileSniffer.Detect(new byte[] { 0x50, 0x4B, 0x03, 0x04 })); // a zip
    }
}
