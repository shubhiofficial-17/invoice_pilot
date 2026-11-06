using InvoicePilot.Api.Ai;

namespace InvoicePilot.Api.Extraction;

/// <summary>What the invoice says, as read by the model. Nothing here is trusted until checked.</summary>
public sealed record ExtractedInvoice(
    string? SupplierName,
    string? SupplierTaxId,
    string? InvoiceNumber,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    string? PoNumber,
    string? Currency,
    IReadOnlyList<ExtractedLine> Lines,
    decimal? Subtotal,
    decimal? TaxTotal,
    decimal? Total);

public sealed record ExtractedLine(
    string Description,
    string? Sku,
    decimal Quantity,
    decimal UnitPrice,
    decimal? TaxRate,
    decimal? Amount);

/// <summary>A problem found in the extracted data (missing field, numbers that don't add up).</summary>
public sealed record ExtractionIssue(string Code, string Message);

/// <param name="Source">"pdf-text" or "image".</param>
/// <param name="Attempts">Model calls made (a second one happens when the first reply isn't valid JSON).</param>
public sealed record ExtractionResult(
    ExtractedInvoice Data,
    IReadOnlyList<ExtractionIssue> Issues,
    string Source,
    string Model,
    int Attempts,
    LlmUsage Usage,
    DateTimeOffset At);
