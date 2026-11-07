namespace InvoicePilot.Api.Extraction;

/// <summary>
/// Checks the extracted invoice against itself: required fields present, and the numbers
/// adding up. These catch misreads (and badly made invoices) before any ERP lookup.
/// </summary>
public static class ExtractionChecks
{
    /// <summary>Rounding allowance per comparison: one currency unit's hundredth plus 0.1%.</summary>
    public static bool Close(decimal a, decimal b) => Math.Abs(a - b) <= 0.01m + 0.001m * Math.Max(Math.Abs(a), Math.Abs(b));

    public static IReadOnlyList<ExtractionIssue> Run(ExtractedInvoice inv)
    {
        var issues = new List<ExtractionIssue>();

        if (inv.SupplierName is null) issues.Add(new("missing_supplier", "The supplier name could not be read."));
        if (inv.InvoiceNumber is null) issues.Add(new("missing_invoice_number", "The invoice number could not be read."));
        if (inv.InvoiceDate is null) issues.Add(new("missing_invoice_date", "The invoice date could not be read."));
        if (inv.PoNumber is null) issues.Add(new("missing_po_number", "The invoice has no purchase order number."));
        if (inv.Currency is null || inv.Currency.Length != 3 || !inv.Currency.All(char.IsAsciiLetterUpper))
        {
            issues.Add(new("unknown_currency", "The currency is missing or not a 3-letter code."));
        }
        if (inv.Total is null) issues.Add(new("missing_total", "The invoice total could not be read."));
        if (inv.Lines.Count == 0)
        {
            issues.Add(new("no_lines", "No line items could be read."));
            return issues;
        }

        for (var i = 0; i < inv.Lines.Count; i++)
        {
            var line = inv.Lines[i];
            if (line.Quantity <= 0 || line.UnitPrice < 0)
            {
                issues.Add(new("invalid_line", $"Line {i + 1} has a non-positive quantity or a negative price."));
            }
            if (line.Amount is { } amount && !Close(amount, line.Quantity * line.UnitPrice))
            {
                issues.Add(new("line_amount_mismatch",
                    $"Line {i + 1}: {line.Quantity} × {line.UnitPrice} = {line.Quantity * line.UnitPrice}, but the line says {amount}."));
            }
        }

        var lineSum = inv.Lines.Sum(l => l.Amount ?? l.Quantity * l.UnitPrice);
        if (inv.Subtotal is { } subtotal && !Close(subtotal, lineSum))
        {
            issues.Add(new("subtotal_mismatch", $"The lines add up to {lineSum}, but the subtotal says {subtotal}."));
        }

        var net = inv.Subtotal ?? lineSum;
        if (inv.Total is { } total && inv.TaxTotal is { } tax && !Close(total, net + tax))
        {
            issues.Add(new("total_mismatch", $"Subtotal {net} + tax {tax} = {net + tax}, but the total says {total}."));
        }

        return issues;
    }
}
