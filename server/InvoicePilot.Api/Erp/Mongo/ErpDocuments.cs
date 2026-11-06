using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace InvoicePilot.Api.Erp.Mongo;

// Storage shapes for MongoDB. Kept separate from the domain records so the domain
// has no database attributes, and so money is always stored as Decimal128
// (never as a double or a string). Dates are stored as UTC midnight.

public sealed class SupplierDoc
{
    [BsonId] public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TaxId { get; set; } = "";
    public string Currency { get; set; } = "";
    public int PaymentTermsDays { get; set; }
    public string RemitEmail { get; set; } = "";
    [BsonRepresentation(BsonType.String)] public SupplierStatus Status { get; set; }

    public static SupplierDoc From(Supplier s) => new()
    {
        Id = s.Id, Name = s.Name, TaxId = s.TaxId, Currency = s.Currency,
        PaymentTermsDays = s.PaymentTermsDays, RemitEmail = s.RemitEmail, Status = s.Status,
    };

    public Supplier ToModel() => new(Id, Name, TaxId, Currency, PaymentTermsDays, RemitEmail, Status);
}

public sealed class PoLineDoc
{
    public int LineNo { get; set; }
    public string Sku { get; set; } = "";
    public string Description { get; set; } = "";
    [BsonRepresentation(BsonType.Decimal128)] public decimal Quantity { get; set; }
    [BsonRepresentation(BsonType.Decimal128)] public decimal UnitPrice { get; set; }
    [BsonRepresentation(BsonType.Decimal128)] public decimal TaxRate { get; set; }
}

public sealed class PurchaseOrderDoc
{
    [BsonId] public string Number { get; set; } = "";
    public string SupplierId { get; set; } = "";
    public string Currency { get; set; } = "";
    public string BuyerEmail { get; set; } = "";
    [BsonRepresentation(BsonType.String)] public PoStatus Status { get; set; }
    public DateTime OrderDate { get; set; }
    public List<PoLineDoc> Lines { get; set; } = new();

    public static PurchaseOrderDoc From(PurchaseOrder o) => new()
    {
        Number = o.Number, SupplierId = o.SupplierId, Currency = o.Currency, BuyerEmail = o.BuyerEmail,
        Status = o.Status, OrderDate = Dates.ToUtc(o.OrderDate),
        Lines = o.Lines.Select(l => new PoLineDoc
        {
            LineNo = l.LineNo, Sku = l.Sku, Description = l.Description,
            Quantity = l.Quantity, UnitPrice = l.UnitPrice, TaxRate = l.TaxRate,
        }).ToList(),
    };

    public PurchaseOrder ToModel() => new(
        Number, SupplierId, Currency, BuyerEmail, Status, Dates.FromUtc(OrderDate),
        Lines.Select(l => new PoLine(l.LineNo, l.Sku, l.Description, l.Quantity, l.UnitPrice, l.TaxRate)).ToList());
}

public sealed class ReceiptLineDoc
{
    public int PoLineNo { get; set; }
    [BsonRepresentation(BsonType.Decimal128)] public decimal QuantityReceived { get; set; }
}

public sealed class GoodsReceiptDoc
{
    [BsonId] public string Number { get; set; } = "";
    public string PoNumber { get; set; } = "";
    public DateTime ReceivedOn { get; set; }
    public List<ReceiptLineDoc> Lines { get; set; } = new();

    public static GoodsReceiptDoc From(GoodsReceipt r) => new()
    {
        Number = r.Number, PoNumber = r.PoNumber, ReceivedOn = Dates.ToUtc(r.ReceivedOn),
        Lines = r.Lines.Select(l => new ReceiptLineDoc { PoLineNo = l.PoLineNo, QuantityReceived = l.QuantityReceived }).ToList(),
    };

    public GoodsReceipt ToModel() => new(
        Number, PoNumber, Dates.FromUtc(ReceivedOn),
        Lines.Select(l => new ReceiptLine(l.PoLineNo, l.QuantityReceived)).ToList());
}

public sealed class PostedInvoiceDoc
{
    /// <summary>"{supplierId}|{INVOICE NUMBER}": the key itself enforces one booking per supplier and number.</summary>
    [BsonId] public string Key { get; set; } = "";
    public string SupplierId { get; set; } = "";
    public string InvoiceNumber { get; set; } = "";
    public DateTime InvoiceDate { get; set; }
    public string? PoNumber { get; set; }
    public string Currency { get; set; } = "";
    [BsonRepresentation(BsonType.Decimal128)] public decimal Total { get; set; }
    [BsonRepresentation(BsonType.String)] public PostedInvoiceStatus Status { get; set; }
    public string? SourceRef { get; set; }

    public static PostedInvoiceDoc From(PostedInvoice i) => new()
    {
        Key = ErpNormalizer.InvoiceKey(i), SupplierId = i.SupplierId, InvoiceNumber = i.InvoiceNumber,
        InvoiceDate = Dates.ToUtc(i.InvoiceDate), PoNumber = i.PoNumber, Currency = i.Currency,
        Total = i.Total, Status = i.Status, SourceRef = i.SourceRef,
    };

    public PostedInvoice ToModel() => new(
        SupplierId, InvoiceNumber, Dates.FromUtc(InvoiceDate), PoNumber, Currency, Total, Status, SourceRef);
}

internal static class Dates
{
    public static DateTime ToUtc(DateOnly d) => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    public static DateOnly FromUtc(DateTime d) => DateOnly.FromDateTime(d.ToUniversalTime());
}
