namespace InvoicePilot.Api.Erp;

/// <summary>Thread-safe in-memory ERP for tests and quick local runs.</summary>
public sealed class InMemoryErpStore : IErpStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Supplier> _suppliers = new();
    private readonly Dictionary<string, PurchaseOrder> _orders = new();
    private readonly Dictionary<string, GoodsReceipt> _receipts = new();
    private readonly Dictionary<string, PostedInvoice> _invoices = new();

    public Task<IReadOnlyList<Supplier>> ListSuppliersAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Supplier>>(
                _suppliers.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList());
        }
    }

    public Task<Supplier?> GetSupplierAsync(string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_suppliers.GetValueOrDefault(ErpKeys.Normalize(id)));
        }
    }

    public Task<IReadOnlyList<PurchaseOrder>> ListPurchaseOrdersAsync(string? supplierId = null, CancellationToken ct = default)
    {
        var supplier = supplierId is null ? null : ErpKeys.Normalize(supplierId);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<PurchaseOrder>>(
                _orders.Values
                    .Where(o => supplier is null || o.SupplierId == supplier)
                    .OrderBy(o => o.Number, StringComparer.Ordinal)
                    .ToList());
        }
    }

    public Task<PurchaseOrder?> GetPurchaseOrderAsync(string number, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_orders.GetValueOrDefault(ErpKeys.Normalize(number)));
        }
    }

    public Task<IReadOnlyList<GoodsReceipt>> ListReceiptsForPoAsync(string poNumber, CancellationToken ct = default)
    {
        var po = ErpKeys.Normalize(poNumber);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<GoodsReceipt>>(
                _receipts.Values
                    .Where(r => r.PoNumber == po)
                    .OrderBy(r => r.ReceivedOn)
                    .ThenBy(r => r.Number, StringComparer.Ordinal)
                    .ToList());
        }
    }

    public Task<IReadOnlyList<PostedInvoice>> ListPostedInvoicesAsync(string supplierId, CancellationToken ct = default)
    {
        var supplier = ErpKeys.Normalize(supplierId);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<PostedInvoice>>(
                _invoices.Values
                    .Where(i => i.SupplierId == supplier)
                    .OrderByDescending(i => i.InvoiceDate)
                    .ThenBy(i => i.InvoiceNumber, StringComparer.Ordinal)
                    .ToList());
        }
    }

    public Task<bool> AddPostedInvoiceAsync(PostedInvoice invoice, CancellationToken ct = default)
    {
        var normalized = ErpNormalizer.Normalize(invoice);
        lock (_gate)
        {
            return Task.FromResult(_invoices.TryAdd(ErpNormalizer.InvoiceKey(normalized), normalized));
        }
    }

    public Task UpsertAsync(ErpData data, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var s in data.Suppliers.Select(ErpNormalizer.Normalize)) _suppliers[s.Id] = s;
            foreach (var o in data.PurchaseOrders.Select(ErpNormalizer.Normalize)) _orders[o.Number] = o;
            foreach (var r in data.Receipts.Select(ErpNormalizer.Normalize)) _receipts[r.Number] = r;
            foreach (var i in data.PostedInvoices.Select(ErpNormalizer.Normalize)) _invoices[ErpNormalizer.InvoiceKey(i)] = i;
        }
        return Task.CompletedTask;
    }

    public Task<bool> IsEmptyAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_suppliers.Count == 0 && _orders.Count == 0);
        }
    }
}

/// <summary>Brings record keys to their stored (upper-case) form, so every store behaves the same.</summary>
public static class ErpNormalizer
{
    public static Supplier Normalize(Supplier s) => s with { Id = ErpKeys.Normalize(s.Id) };

    public static PurchaseOrder Normalize(PurchaseOrder o) => o with
    {
        Number = ErpKeys.Normalize(o.Number),
        SupplierId = ErpKeys.Normalize(o.SupplierId),
        Lines = o.Lines.ToList(),
    };

    public static GoodsReceipt Normalize(GoodsReceipt r) => r with
    {
        Number = ErpKeys.Normalize(r.Number),
        PoNumber = ErpKeys.Normalize(r.PoNumber),
        Lines = r.Lines.ToList(),
    };

    public static PostedInvoice Normalize(PostedInvoice i) => i with
    {
        SupplierId = ErpKeys.Normalize(i.SupplierId),
        InvoiceNumber = i.InvoiceNumber.Trim(),
        PoNumber = i.PoNumber is null ? null : ErpKeys.Normalize(i.PoNumber),
    };

    /// <summary>One booked invoice per supplier and invoice number (number compared case-insensitively).</summary>
    public static string InvoiceKey(PostedInvoice i) =>
        $"{ErpKeys.Normalize(i.SupplierId)}|{ErpKeys.Normalize(i.InvoiceNumber)}";
}
