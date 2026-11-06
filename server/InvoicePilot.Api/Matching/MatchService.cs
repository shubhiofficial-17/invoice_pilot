using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Extraction;

namespace InvoicePilot.Api.Matching;

/// <summary>Loads what the match needs from the ERP, then runs the pure engine.</summary>
public sealed class MatchService(IErpStore erp)
{
    public async Task<MatchContext> LoadContextAsync(ExtractedInvoice inv, CancellationToken ct = default)
    {
        var suppliers = await erp.ListSuppliersAsync(ct);
        var po = inv.PoNumber is null ? null : await erp.GetPurchaseOrderAsync(inv.PoNumber, ct);
        IReadOnlyList<GoodsReceipt> receipts = po is null ? [] : await erp.ListReceiptsForPoAsync(po.Number, ct);

        var supplier = MatchEngine.ResolveSupplier(inv, suppliers, out _)
            ?? (po is null ? null : suppliers.FirstOrDefault(s => s.Id == po.SupplierId));
        IReadOnlyList<PostedInvoice> history = supplier is null ? [] : await erp.ListPostedInvoicesAsync(supplier.Id, ct);

        return new MatchContext(suppliers, po, receipts, history);
    }

    public async Task<MatchReport> MatchAsync(ExtractionResult extraction, CancellationToken ct = default)
    {
        var context = await LoadContextAsync(extraction.Data, ct);
        return MatchEngine.Run(extraction.Data, context, extraction.Issues);
    }
}
