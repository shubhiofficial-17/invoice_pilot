using InvoicePilot.Api.Ai;
using InvoicePilot.Api.Extraction;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace InvoicePilot.Api.Invoices.Mongo;

public sealed class ExtractedLineDoc
{
    public string Description { get; set; } = "";
    public string? Sku { get; set; }
    [BsonRepresentation(BsonType.Decimal128)] public decimal Quantity { get; set; }
    [BsonRepresentation(BsonType.Decimal128)] public decimal UnitPrice { get; set; }
    [BsonRepresentation(BsonType.Decimal128)] public decimal? TaxRate { get; set; }
    [BsonRepresentation(BsonType.Decimal128)] public decimal? Amount { get; set; }
}

public sealed class ExtractionDoc
{
    public string? SupplierName { get; set; }
    public string? SupplierTaxId { get; set; }
    public string? InvoiceNumber { get; set; }
    public DateTime? InvoiceDate { get; set; }
    public DateTime? DueDate { get; set; }
    public string? PoNumber { get; set; }
    public string? Currency { get; set; }
    public List<ExtractedLineDoc> Lines { get; set; } = new();
    [BsonRepresentation(BsonType.Decimal128)] public decimal? Subtotal { get; set; }
    [BsonRepresentation(BsonType.Decimal128)] public decimal? TaxTotal { get; set; }
    [BsonRepresentation(BsonType.Decimal128)] public decimal? Total { get; set; }
    public List<ExtractionIssueDoc> Issues { get; set; } = new();
    public string Source { get; set; } = "";
    public string Model { get; set; } = "";
    public int Attempts { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public DateTime At { get; set; }

    public static ExtractionDoc From(ExtractionResult r) => new()
    {
        SupplierName = r.Data.SupplierName,
        SupplierTaxId = r.Data.SupplierTaxId,
        InvoiceNumber = r.Data.InvoiceNumber,
        InvoiceDate = ToUtc(r.Data.InvoiceDate),
        DueDate = ToUtc(r.Data.DueDate),
        PoNumber = r.Data.PoNumber,
        Currency = r.Data.Currency,
        Lines = r.Data.Lines.Select(l => new ExtractedLineDoc
        {
            Description = l.Description, Sku = l.Sku, Quantity = l.Quantity,
            UnitPrice = l.UnitPrice, TaxRate = l.TaxRate, Amount = l.Amount,
        }).ToList(),
        Subtotal = r.Data.Subtotal,
        TaxTotal = r.Data.TaxTotal,
        Total = r.Data.Total,
        Issues = r.Issues.Select(i => new ExtractionIssueDoc { Code = i.Code, Message = i.Message }).ToList(),
        Source = r.Source,
        Model = r.Model,
        Attempts = r.Attempts,
        PromptTokens = r.Usage.PromptTokens,
        CompletionTokens = r.Usage.CompletionTokens,
        At = r.At.UtcDateTime,
    };

    public ExtractionResult ToModel() => new(
        new ExtractedInvoice(
            SupplierName, SupplierTaxId, InvoiceNumber, FromUtc(InvoiceDate), FromUtc(DueDate), PoNumber, Currency,
            Lines.Select(l => new ExtractedLine(l.Description, l.Sku, l.Quantity, l.UnitPrice, l.TaxRate, l.Amount)).ToList(),
            Subtotal, TaxTotal, Total),
        Issues.Select(i => new ExtractionIssue(i.Code, i.Message)).ToList(),
        Source,
        Model,
        Attempts,
        new LlmUsage(PromptTokens, CompletionTokens),
        new DateTimeOffset(DateTime.SpecifyKind(At, DateTimeKind.Utc)));

    private static DateTime? ToUtc(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static DateOnly? FromUtc(DateTime? d) => d is null ? null : DateOnly.FromDateTime(d.Value.ToUniversalTime());
}

public sealed class ExtractionIssueDoc
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}
