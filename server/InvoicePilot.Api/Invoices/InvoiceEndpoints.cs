using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;

namespace InvoicePilot.Api.Invoices;

public static class InvoiceLimits
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
}

public static class InvoiceEndpoints
{
    public static IEndpointRouteBuilder MapInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var invoices = app.MapGroup("/api/invoices").WithTags("Invoices");

        // Same-origin JSON/multipart API; antiforgery tokens aren't used (see ADR 0003).
        invoices.MapPost("", UploadAsync).DisableAntiforgery();

        invoices.MapGet("", async (int? limit, IInvoiceStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(Math.Clamp(limit ?? 100, 1, 500), ct)));

        invoices.MapGet("/{id}", async (string id, IInvoiceStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } invoice ? Results.Ok(invoice) : Results.NotFound());

        invoices.MapGet("/{id}/file", async (string id, IInvoiceStore store, HttpContext http, CancellationToken ct) =>
        {
            var invoice = await store.GetAsync(id, ct);
            var bytes = invoice is null ? null : await store.GetFileAsync(id, ct);
            if (invoice is null || bytes is null) return Results.NotFound();

            var disposition = new ContentDispositionHeaderValue("inline");
            disposition.SetHttpFileName(invoice.File.FileName);
            http.Response.Headers.ContentDisposition = disposition.ToString();
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(bytes, invoice.File.ContentType);
        });

        return app;
    }

    private static async Task<IResult> UploadAsync(
        IFormFile? file, IInvoiceStore store, TimeProvider clock, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Choose a PDF, PNG or JPEG file."] });
        }
        if (file.Length > InvoiceLimits.MaxFileBytes)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The file is larger than 10 MB."] });
        }

        byte[] content;
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await file.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }

        var contentType = FileSniffer.Detect(content.AsSpan(0, Math.Min(content.Length, 16)));
        if (contentType is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "Unsupported file type",
                detail: "Upload a PDF, PNG or JPEG invoice.");
        }

        // Millisecond precision: that's what MongoDB stores, so values round-trip exactly.
        var utc = clock.GetUtcNow();
        var now = new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var invoice = new Invoice(
            InvoiceIds.New(),
            InvoiceStatus.Received,
            new InvoiceFile(
                FileSniffer.SafeFileName(file.FileName, contentType),
                contentType,
                content.Length,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()),
            now,
            now,
            Version: 1);

        var (stored, created) = await store.AddAsync(invoice, content, ct);

        // Uploading the same file twice is harmless: you get the invoice that already exists.
        return created
            ? Results.Created($"/api/invoices/{stored.Id}", stored)
            : Results.Ok(stored);
    }
}
