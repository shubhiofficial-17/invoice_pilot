using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Extraction;

namespace InvoicePilot.Api.Matching;

/// <summary>
/// Deterministic 3-way match: invoice vs purchase order vs goods receipts, plus supplier
/// status and duplicate checks. Same input, same report, every time. No model involved.
/// </summary>
public static class MatchEngine
{
    private static readonly HashSet<string> NoiseWords =
        ["pvt", "private", "ltd", "limited", "inc", "llp", "llc", "co", "company", "corp", "the", "and"];

    public static MatchReport Run(ExtractedInvoice inv, MatchContext ctx, IReadOnlyList<ExtractionIssue>? extractionIssues = null)
    {
        var findings = new List<Finding>();
        var po = ctx.PurchaseOrder;

        // 1. Who sent it?
        var supplier = ResolveSupplier(inv, ctx.Suppliers, out var resolvedBy);
        if (supplier is null && po is not null && inv.SupplierName is null && inv.SupplierTaxId is null)
        {
            supplier = ctx.Suppliers.FirstOrDefault(s => s.Id == po.SupplierId);
            resolvedBy = "po";
            findings.Add(new("supplier_unverified", Severity.Warning,
                "The supplier name and tax id could not be read; the supplier was taken from the PO."));
        }
        if (supplier is null)
        {
            findings.Add(new("supplier_not_found", Severity.Block,
                $"No supplier in the ERP matches \"{inv.SupplierName ?? inv.SupplierTaxId ?? "(unreadable)"}\"."));
        }
        else if (supplier.Status == SupplierStatus.Blocked)
        {
            findings.Add(new("supplier_blocked", Severity.Block, $"{supplier.Name} ({supplier.Id}) is blocked in the ERP."));
        }

        // 2. Which PO?
        if (inv.PoNumber is null)
        {
            findings.Add(new("missing_po_number", Severity.Exception, "The invoice has no PO number."));
        }
        else if (po is null)
        {
            findings.Add(new("po_not_found", Severity.Exception, $"PO {inv.PoNumber} does not exist in the ERP."));
        }
        else
        {
            if (po.Status == PoStatus.Cancelled)
                findings.Add(new("po_cancelled", Severity.Block, $"PO {po.Number} is cancelled."));
            else if (po.Status == PoStatus.Closed)
                findings.Add(new("po_closed", Severity.Exception, $"PO {po.Number} is closed."));

            if (supplier is not null && po.SupplierId != supplier.Id)
                findings.Add(new("supplier_not_on_po", Severity.Exception,
                    $"PO {po.Number} belongs to {po.SupplierId}, but the invoice is from {supplier.Id}."));

            if (inv.Currency is not null && inv.Currency != po.Currency)
                findings.Add(new("currency_mismatch", Severity.Exception,
                    $"The invoice is in {inv.Currency}, but PO {po.Number} is in {po.Currency}."));
        }

        // 3. Already booked?
        if (supplier is not null)
        {
            var number = inv.InvoiceNumber is null ? null : NormalizeInvoiceNumber(inv.InvoiceNumber);
            var exact = number is null ? null : ctx.SupplierHistory.FirstOrDefault(h => NormalizeInvoiceNumber(h.InvoiceNumber) == number);
            if (exact is not null)
            {
                findings.Add(new("duplicate_invoice", Severity.Block,
                    $"Invoice {exact.InvoiceNumber} from {supplier.Id} was already booked on {exact.InvoiceDate:yyyy-MM-dd} ({exact.Status.ToString().ToLowerInvariant()}).",
                    Expected: exact.Total, Actual: inv.Total));
            }
            else if (inv.Total is { } total && inv.InvoiceDate is { } date)
            {
                var similar = ctx.SupplierHistory.FirstOrDefault(h => h.Total == total && Math.Abs(h.InvoiceDate.DayNumber - date.DayNumber) <= 3);
                if (similar is not null)
                {
                    findings.Add(new("possible_duplicate", Severity.Exception,
                        $"Booked invoice {similar.InvoiceNumber} has the same total ({total}) and a date within 3 days."));
                }
            }
        }

        // 4. Lines against the PO and the goods receipts.
        var lines = new List<LineMatch>();
        decimal? expectedTotal = null;
        if (po is not null)
        {
            var received = ctx.Receipts.SelectMany(r => r.Lines)
                .GroupBy(l => l.PoLineNo)
                .ToDictionary(g => g.Key, g => g.Sum(l => l.QuantityReceived));
            var billedPerPoLine = new Dictionary<int, decimal>();
            decimal expectedNet = 0, expectedTax = 0;

            for (var i = 0; i < inv.Lines.Count; i++)
            {
                var line = inv.Lines[i];
                var n = i + 1;
                var (poLine, by) = MatchLine(line, po.Lines);
                if (poLine is null)
                {
                    findings.Add(new("unmatched_line", Severity.Exception,
                        $"Invoice line {n} (\"{line.Description}\") is not on PO {po.Number}.", InvoiceLine: n,
                        AmountImpact: line.Quantity * line.UnitPrice));
                    lines.Add(new(n, null, "none", line.Description, line.Quantity, null, null, line.UnitPrice, null, null, line.TaxRate, null));
                    continue;
                }

                billedPerPoLine[poLine.LineNo] = billedPerPoLine.GetValueOrDefault(poLine.LineNo) + line.Quantity;
                var variance = poLine.UnitPrice == 0 ? (decimal?)null
                    : Math.Round((line.UnitPrice - poLine.UnitPrice) / poLine.UnitPrice * 100m, 2);
                var net = line.Quantity * line.UnitPrice;
                expectedNet += net;
                expectedTax += net * poLine.TaxRate;

                if (variance is > 0)
                {
                    findings.Add(new("price_variance", Severity.Warning,
                        $"Line {n}: billed {line.UnitPrice} per unit, PO {po.Number} line {poLine.LineNo} says {poLine.UnitPrice} (+{variance}%).",
                        n, poLine.LineNo, poLine.UnitPrice, line.UnitPrice, variance, (line.UnitPrice - poLine.UnitPrice) * line.Quantity));
                }
                else if (variance is < 0)
                {
                    findings.Add(new("price_below_po", Severity.Info,
                        $"Line {n}: billed {line.UnitPrice}, below the PO price {poLine.UnitPrice} ({variance}%).",
                        n, poLine.LineNo, poLine.UnitPrice, line.UnitPrice, variance));
                }

                if (line.TaxRate is { } rate && rate != poLine.TaxRate)
                {
                    findings.Add(new("tax_rate_mismatch", Severity.Exception,
                        $"Line {n}: tax rate {rate:P0} on the invoice, {poLine.TaxRate:P0} on the PO.",
                        n, poLine.LineNo, poLine.TaxRate, rate));
                }

                lines.Add(new(n, poLine.LineNo, by, line.Description, line.Quantity, poLine.Quantity,
                    received.GetValueOrDefault(poLine.LineNo), line.UnitPrice, poLine.UnitPrice, variance, line.TaxRate, poLine.TaxRate));
            }

            foreach (var (poLineNo, billed) in billedPerPoLine.OrderBy(kv => kv.Key))
            {
                var poLine = po.Lines.Single(l => l.LineNo == poLineNo);
                var got = received.GetValueOrDefault(poLineNo);
                if (billed > got)
                {
                    findings.Add(new("quantity_exceeds_received", Severity.Exception,
                        got == 0
                            ? $"PO line {poLineNo}: {billed} billed, but nothing has been received yet."
                            : $"PO line {poLineNo}: {billed} billed, but only {got} received.",
                        PoLine: poLineNo, Expected: got, Actual: billed, AmountImpact: (billed - got) * poLine.UnitPrice));
                }
                if (billed > poLine.Quantity)
                {
                    findings.Add(new("quantity_exceeds_ordered", Severity.Exception,
                        $"PO line {poLineNo}: {billed} billed, but only {poLine.Quantity} ordered.",
                        PoLine: poLineNo, Expected: poLine.Quantity, Actual: billed));
                }
            }

            expectedTax = Math.Round(expectedTax, 2);
            expectedTotal = expectedNet + expectedTax;
            if (inv.TaxTotal is { } tax && !ExtractionChecks.Close(tax, expectedTax) && !findings.Any(f => f.Code == "tax_rate_mismatch"))
            {
                findings.Add(new("tax_amount_mismatch", Severity.Exception,
                    $"Tax on the invoice is {tax}; at the PO's tax rates it should be {expectedTax}.",
                    Expected: expectedTax, Actual: tax));
            }

            // Booked before against this PO plus this invoice must not exceed the PO's value.
            var poGross = po.Lines.Sum(l => l.Quantity * l.UnitPrice * (1 + l.TaxRate));
            var alreadyBooked = ctx.SupplierHistory.Where(h => h.PoNumber == po.Number).Sum(h => h.Total);
            if (alreadyBooked > 0 && inv.Total is { } t && alreadyBooked + t > poGross + 0.01m
                && !findings.Any(f => f.Code == "duplicate_invoice"))
            {
                findings.Add(new("po_amount_exceeded", Severity.Exception,
                    $"{alreadyBooked} is already booked against PO {po.Number} (worth {Math.Round(poGross, 2)}); this invoice would exceed it.",
                    Expected: poGross - alreadyBooked, Actual: t));
            }
        }

        // 5. The invoice's own arithmetic and readability (from extraction).
        foreach (var issue in extractionIssues ?? Array.Empty<ExtractionIssue>())
        {
            var severity = issue.Code switch
            {
                "missing_po_number" or "missing_supplier" => (Severity?)null, // covered above
                "missing_invoice_date" => Severity.Warning,
                _ => Severity.Exception,
            };
            if (severity is { } s) findings.Add(new(issue.Code, s, issue.Message));
        }

        // 6. Payment terms.
        if (supplier is not null && inv.InvoiceDate is { } invoiceDate && inv.DueDate is { } due
            && due < invoiceDate.AddDays(supplier.PaymentTermsDays))
        {
            findings.Add(new("payment_terms_shorter", Severity.Warning,
                $"Due {due:yyyy-MM-dd}, but {supplier.Id}'s terms are {supplier.PaymentTermsDays} days (due {invoiceDate.AddDays(supplier.PaymentTermsDays):yyyy-MM-dd})."));
        }

        return new MatchReport(
            supplier?.Id, supplier?.Name, po?.Number, inv.Currency ?? po?.Currency, inv.Total, expectedTotal,
            lines, findings.OrderByDescending(f => f.Severity).ToList());
    }

    public static Supplier? ResolveSupplier(ExtractedInvoice inv, IReadOnlyList<Supplier> suppliers, out string resolvedBy)
    {
        resolvedBy = "none";
        if (inv.SupplierTaxId is { } taxId)
        {
            var key = NormalizeKey(taxId);
            var byTax = suppliers.FirstOrDefault(s => NormalizeKey(s.TaxId) == key);
            if (byTax is not null)
            {
                resolvedBy = "taxId";
                return byTax;
            }
        }
        if (inv.SupplierName is { } name)
        {
            var tokens = NameTokens(name);
            var best = suppliers
                .Select(s => (Supplier: s, Score: Jaccard(tokens, NameTokens(s.Name))))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (best.Supplier is not null && best.Score >= 0.6)
            {
                resolvedBy = "name";
                return best.Supplier;
            }
        }
        return null;
    }

    private static (PoLine? Line, string By) MatchLine(ExtractedLine line, IReadOnlyList<PoLine> poLines)
    {
        if (line.Sku is { } sku)
        {
            var key = NormalizeKey(sku);
            var bySku = poLines.FirstOrDefault(p => NormalizeKey(p.Sku) == key);
            if (bySku is not null) return (bySku, "sku");
        }
        var tokens = NameTokens(line.Description);
        var best = poLines
            .Select(p => (Line: p, Score: Jaccard(tokens, NameTokens(p.Description))))
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();
        return best.Line is not null && best.Score >= 0.5 ? (best.Line, "description") : (null, "none");
    }

    /// <summary>"AC/2026/0420", "ac-2026-0420" and "AC 2026 0420" are the same invoice number.</summary>
    public static string NormalizeInvoiceNumber(string number) => NormalizeKey(number);

    private static string NormalizeKey(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static HashSet<string> NameTokens(string s) =>
        s.ToLowerInvariant()
            .Split([' ', ',', '.', '-', '/', '(', ')', '&'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !NoiseWords.Contains(t))
            .ToHashSet();

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var common = a.Count(b.Contains);
        return (double)common / (a.Count + b.Count - common);
    }
}
