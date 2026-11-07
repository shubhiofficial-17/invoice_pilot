using InvoicePilot.Api.Erp;

namespace InvoicePilot.Api.Matching;

/// <summary>
/// How serious a finding is. The match engine only measures; the AP policy (milestone 6)
/// decides what each finding means for the invoice.
/// Info: worth knowing. Warning: within normal tolerances, policy decides.
/// Exception: needs a person or the supplier. Block: the invoice must not be paid.
/// </summary>
public enum Severity { Info, Warning, Exception, Block }

public sealed record Finding(
    string Code,
    Severity Severity,
    string Message,
    int? InvoiceLine = null,
    int? PoLine = null,
    decimal? Expected = null,
    decimal? Actual = null,
    decimal? VariancePct = null,
    decimal? AmountImpact = null);

/// <param name="MatchedBy">"sku", "description" or "none".</param>
public sealed record LineMatch(
    int InvoiceLine,
    int? PoLine,
    string MatchedBy,
    string Description,
    decimal BilledQuantity,
    decimal? OrderedQuantity,
    decimal? ReceivedQuantity,
    decimal BilledUnitPrice,
    decimal? PoUnitPrice,
    decimal? PriceVariancePct,
    decimal? BilledTaxRate,
    decimal? PoTaxRate);

public sealed record MatchReport(
    string? SupplierId,
    string? SupplierName,
    string? PoNumber,
    string? Currency,
    decimal? InvoiceTotal,
    decimal? ExpectedTotal,
    IReadOnlyList<LineMatch> Lines,
    IReadOnlyList<Finding> Findings)
{
    public bool HasBlock => Findings.Any(f => f.Severity == Severity.Block);

    public bool IsClean => Findings.All(f => f.Severity == Severity.Info);
}

/// <summary>Everything the engine needs from the ERP, loaded up front so the engine itself is pure.</summary>
public sealed record MatchContext(
    IReadOnlyList<Supplier> Suppliers,
    PurchaseOrder? PurchaseOrder,
    IReadOnlyList<GoodsReceipt> Receipts,
    IReadOnlyList<PostedInvoice> SupplierHistory);
