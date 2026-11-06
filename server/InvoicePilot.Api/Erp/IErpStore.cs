namespace InvoicePilot.Api.Erp;

public interface IErpStore
{
    Task<IReadOnlyList<Supplier>> ListSuppliersAsync(CancellationToken ct = default);

    Task<Supplier?> GetSupplierAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<PurchaseOrder>> ListPurchaseOrdersAsync(string? supplierId = null, CancellationToken ct = default);

    Task<PurchaseOrder?> GetPurchaseOrderAsync(string number, CancellationToken ct = default);

    /// <summary>Receipts for a PO, oldest first.</summary>
    Task<IReadOnlyList<GoodsReceipt>> ListReceiptsForPoAsync(string poNumber, CancellationToken ct = default);

    /// <summary>Invoices already booked for a supplier, newest first.</summary>
    Task<IReadOnlyList<PostedInvoice>> ListPostedInvoicesAsync(string supplierId, CancellationToken ct = default);

    /// <summary>Books an invoice. Returns false if this supplier already has an invoice with that number.</summary>
    Task<bool> AddPostedInvoiceAsync(PostedInvoice invoice, CancellationToken ct = default);

    /// <summary>Inserts or replaces every record (matched by its key).</summary>
    Task UpsertAsync(ErpData data, CancellationToken ct = default);

    Task<bool> IsEmptyAsync(CancellationToken ct = default);
}
