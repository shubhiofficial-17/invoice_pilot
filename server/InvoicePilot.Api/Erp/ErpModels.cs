namespace InvoicePilot.Api.Erp;

// The mock ERP: the "system of record" the agent's tools query.
// Amounts are decimals in the document currency. Tax rates are fractions (0.18 = 18%).

public enum SupplierStatus { Active, Blocked }

public sealed record Supplier(
    string Id,
    string Name,
    string TaxId,
    string Currency,
    int PaymentTermsDays,
    string RemitEmail,
    SupplierStatus Status);

public enum PoStatus { Open, Closed, Cancelled }

public sealed record PoLine(
    int LineNo,
    string Sku,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxRate);

public sealed record PurchaseOrder(
    string Number,
    string SupplierId,
    string Currency,
    string BuyerEmail,
    PoStatus Status,
    DateOnly OrderDate,
    IReadOnlyList<PoLine> Lines);

public sealed record ReceiptLine(int PoLineNo, decimal QuantityReceived);

public sealed record GoodsReceipt(
    string Number,
    string PoNumber,
    DateOnly ReceivedOn,
    IReadOnlyList<ReceiptLine> Lines);

public enum PostedInvoiceStatus { Posted, Paid }

/// <summary>An invoice already booked in the ERP. Used to catch duplicates.</summary>
public sealed record PostedInvoice(
    string SupplierId,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    string? PoNumber,
    string Currency,
    decimal Total,
    PostedInvoiceStatus Status);

public sealed record ErpData(
    IReadOnlyList<Supplier> Suppliers,
    IReadOnlyList<PurchaseOrder> PurchaseOrders,
    IReadOnlyList<GoodsReceipt> Receipts,
    IReadOnlyList<PostedInvoice> PostedInvoices);

public static class ErpKeys
{
    /// <summary>ERP identifiers are stored upper-case; lookups ignore case and surrounding spaces.</summary>
    public static string Normalize(string key) => key.Trim().ToUpperInvariant();
}
