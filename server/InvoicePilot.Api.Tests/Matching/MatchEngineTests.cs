using System.Net;
using System.Net.Http.Json;
using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Extraction;
using InvoicePilot.Api.Matching;
using InvoicePilot.Api.Tests.Extraction;

namespace InvoicePilot.Api.Tests.Matching;

public class MatchEngineTests
{
    private static readonly ErpData Seed = ErpSeed.Create();

    /// <summary>An invoice that bills a seeded PO exactly as ordered (with correct totals).</summary>
    public static ExtractedInvoice Billing(string poNumber, string invoiceNumber = "INV-T-1", Func<int, ExtractedLine, ExtractedLine>? change = null)
    {
        var po = Seed.PurchaseOrders.Single(p => p.Number == poNumber);
        var supplier = Seed.Suppliers.Single(s => s.Id == po.SupplierId);
        var lines = po.Lines.Select((l, i) =>
        {
            var line = new ExtractedLine(l.Description, l.Sku, l.Quantity, l.UnitPrice, l.TaxRate, l.Quantity * l.UnitPrice);
            return change is null ? line : change(i + 1, line);
        }).ToList();
        var subtotal = lines.Sum(l => l.Quantity * l.UnitPrice);
        var tax = Math.Round(lines.Sum(l => l.Quantity * l.UnitPrice * (l.TaxRate ?? 0)), 2);
        return new ExtractedInvoice(supplier.Name, supplier.TaxId, invoiceNumber, new DateOnly(2026, 11, 2),
            new DateOnly(2026, 11, 2).AddDays(supplier.PaymentTermsDays), po.Number, po.Currency, lines, subtotal, tax, subtotal + tax);
    }

    private static MatchReport Match(ExtractedInvoice inv)
    {
        var po = inv.PoNumber is null ? null : Seed.PurchaseOrders.FirstOrDefault(p => p.Number == inv.PoNumber);
        var supplier = MatchEngine.ResolveSupplier(inv, Seed.Suppliers, out _)
            ?? (po is null ? null : Seed.Suppliers.First(s => s.Id == po.SupplierId));
        var context = new MatchContext(
            Seed.Suppliers,
            po,
            po is null ? [] : Seed.Receipts.Where(r => r.PoNumber == po.Number).ToList(),
            supplier is null ? [] : Seed.PostedInvoices.Where(h => h.SupplierId == supplier.Id).ToList());
        return MatchEngine.Run(inv, context, ExtractionChecks.Run(inv));
    }

    private static IEnumerable<string> Codes(MatchReport r) => r.Findings.Select(f => f.Code);

    [Theory]
    [InlineData("PO-1001")]
    [InlineData("PO-1002")]
    [InlineData("PO-1004")]
    public void An_invoice_billed_exactly_as_ordered_and_received_is_clean(string po)
    {
        var report = Match(Billing(po));

        Assert.True(report.IsClean, string.Join("; ", report.Findings.Select(f => f.Message)));
        Assert.Equal(po, report.PoNumber);
        Assert.All(report.Lines, l => Assert.Equal("sku", l.MatchedBy));
        Assert.Equal(report.InvoiceTotal, report.ExpectedTotal);
    }

    [Fact]
    public void A_price_increase_is_measured()
    {
        var report = Match(Billing("PO-1001", change: (n, l) => n == 1 ? l with { UnitPrice = 4.59m, Amount = 4590m } : l));

        var finding = Assert.Single(report.Findings, f => f.Code == "price_variance");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(2.00m, finding.VariancePct);
        Assert.Equal(90m, finding.AmountImpact);
        Assert.Equal(1, finding.InvoiceLine);
    }

    [Fact]
    public void A_lower_price_is_only_information()
    {
        var report = Match(Billing("PO-1001", change: (n, l) => n == 2 ? l with { UnitPrice = 1.10m, Amount = 1100m } : l));

        Assert.Equal(Severity.Info, Assert.Single(report.Findings).Severity);
    }

    [Fact]
    public void Billing_more_than_was_received_is_an_exception()
    {
        var report = Match(Billing("PO-1005")); // 500 billed, 300 received

        var finding = Assert.Single(report.Findings, f => f.Code == "quantity_exceeds_received");
        Assert.Equal(Severity.Exception, finding.Severity);
        Assert.Equal(300m, finding.Expected);
        Assert.Equal(500m, finding.Actual);
        Assert.Equal(200m * 85m, finding.AmountImpact);
    }

    [Fact]
    public void Billing_only_what_was_received_is_clean()
    {
        var report = Match(Billing("PO-1005", change: (_, l) => l with { Quantity = 300, Amount = 300 * l.UnitPrice }));

        Assert.True(report.IsClean);
    }

    [Fact]
    public void Nothing_received_yet_is_called_out()
    {
        var report = Match(Billing("PO-1008"));

        Assert.Equal(2, report.Findings.Count(f => f.Code == "quantity_exceeds_received"));
        Assert.Contains("nothing has been received", report.Findings[0].Message);
    }

    [Fact]
    public void A_resubmitted_invoice_is_a_duplicate_even_written_differently()
    {
        var report = Match(Billing("PO-1003", invoiceNumber: "nw 55012"));

        var finding = Assert.Single(report.Findings, f => f.Code == "duplicate_invoice");
        Assert.Equal(Severity.Block, finding.Severity);
        Assert.True(report.HasBlock);
    }

    [Fact]
    public void Rebilling_a_fully_invoiced_po_under_a_new_number_exceeds_the_po()
    {
        var report = Match(Billing("PO-1003", invoiceNumber: "NW-55999"));

        Assert.Contains("po_amount_exceeded", Codes(report));
        Assert.DoesNotContain("duplicate_invoice", Codes(report));
    }

    [Fact]
    public void A_blocked_supplier_and_a_cancelled_po_block_payment()
    {
        Assert.Contains(Match(Billing("PO-1006")).Findings, f => f.Code == "supplier_blocked" && f.Severity == Severity.Block);
        Assert.Contains(Match(Billing("PO-1006")).Findings, f => f.Code == "po_closed");
        Assert.Contains(Match(Billing("PO-1007")).Findings, f => f.Code == "po_cancelled" && f.Severity == Severity.Block);
    }

    [Fact]
    public void Missing_or_unknown_po_numbers_are_exceptions()
    {
        Assert.Contains("missing_po_number", Codes(Match(Billing("PO-1001") with { PoNumber = null })));
        Assert.Contains("po_not_found", Codes(Match(Billing("PO-1001") with { PoNumber = "PO-4040" })));
    }

    [Fact]
    public void A_wrong_tax_rate_is_flagged_once()
    {
        var inv = Billing("PO-1002", change: (n, l) => n == 1 ? l with { TaxRate = 0.18m } : l);

        var codes = Codes(Match(inv)).ToList();

        Assert.Contains("tax_rate_mismatch", codes);
        Assert.DoesNotContain("tax_amount_mismatch", codes);
    }

    [Fact]
    public void A_wrong_tax_amount_with_the_right_rates_is_flagged()
    {
        var inv = Billing("PO-1001");
        inv = inv with { TaxTotal = inv.TaxTotal + 50m, Total = inv.Total + 50m };

        Assert.Contains("tax_amount_mismatch", Codes(Match(inv)));
    }

    [Fact]
    public void An_invoice_line_not_on_the_po_is_flagged()
    {
        var inv = Billing("PO-1001");
        var lines = inv.Lines.Append(new ExtractedLine("Express delivery surcharge", null, 1, 500m, 0.18m, 500m)).ToList();
        inv = inv with { Lines = lines, Subtotal = inv.Subtotal + 500m, TaxTotal = inv.TaxTotal + 90m, Total = inv.Total + 590m };

        var finding = Assert.Single(Match(inv).Findings, f => f.Code == "unmatched_line");
        Assert.Equal(3, finding.InvoiceLine);
    }

    [Fact]
    public void Lines_without_sku_match_by_description()
    {
        var report = Match(Billing("PO-1001", change: (_, l) => l with { Sku = null, Description = l.Description.ToUpperInvariant() + " (zinc)" }));

        Assert.All(report.Lines, l => Assert.Equal("description", l.MatchedBy));
        Assert.True(report.IsClean);
    }

    [Fact]
    public void Supplier_is_found_by_tax_id_or_by_name_without_legal_suffixes()
    {
        var byTax = Billing("PO-1001") with { SupplierName = "Totally Different Name", SupplierTaxId = "27 AAACA 1234 F1Z5" };
        var byName = Billing("PO-1001") with { SupplierName = "ACME Industrial Supplies Private Limited", SupplierTaxId = null };

        Assert.Equal("SUP-ACME", Match(byTax).SupplierId);
        Assert.Equal("SUP-ACME", Match(byName).SupplierId);
    }

    [Fact]
    public void An_unknown_supplier_blocks_and_a_supplier_not_on_the_po_is_an_exception()
    {
        var stranger = Billing("PO-1001") with { SupplierName = "Shady Traders", SupplierTaxId = "XX999" };
        var otherSupplier = Billing("PO-1001") with { SupplierName = "Bright Office Co", SupplierTaxId = null };

        Assert.Contains(Match(stranger).Findings, f => f.Code == "supplier_not_found" && f.Severity == Severity.Block);
        Assert.Contains("supplier_not_on_po", Codes(Match(otherSupplier)));
    }

    [Fact]
    public void Currency_and_payment_terms_are_checked()
    {
        var inv = Billing("PO-1001") with { Currency = "USD", DueDate = new DateOnly(2026, 11, 9) };

        var codes = Codes(Match(inv)).ToList();

        Assert.Contains("currency_mismatch", codes);
        Assert.Contains("payment_terms_shorter", codes);
    }

    [Fact]
    public void Invoice_arithmetic_problems_are_carried_into_the_match()
    {
        var inv = Billing("PO-1001");

        Assert.Contains("total_mismatch", Codes(Match(inv with { Total = inv.Total + 100m })));
    }

    [Fact]
    public void Findings_are_ordered_most_serious_first()
    {
        var report = Match(Billing("PO-1006"));

        Assert.Equal(report.Findings.OrderByDescending(f => f.Severity).Select(f => f.Code), Codes(report));
    }

    [Theory]
    [InlineData("AC/2026/0420", "ac-2026-0420")]
    [InlineData("NW 55012", "nw55012")]
    public void Invoice_numbers_compare_without_punctuation_or_case(string a, string b)
    {
        Assert.Equal(MatchEngine.NormalizeInvoiceNumber(a), MatchEngine.NormalizeInvoiceNumber(b));
    }
}

public class MatchEndpointTests(AiAppFactory factory) : IClassFixture<AiAppFactory>
{
    [Fact]
    public async Task A_read_invoice_can_be_matched_against_the_erp()
    {
        factory.Llm.Reply = _ => SampleReplies.AcmeJson;
        var client = factory.CreateClient();
        var uploaded = await client.UploadAsync(TestPdfs.WithLines("Acme invoice", Guid.NewGuid().ToString()));
        await client.WaitUntilReadAsync(uploaded.Id);

        var report = await client.GetFromJsonAsync<MatchReport>($"/api/invoices/{uploaded.Id}/match", TestJson.Options);

        Assert.Equal("SUP-ACME", report!.SupplierId);
        Assert.Equal("PO-1001", report.PoNumber);
        Assert.True(report.IsClean);
    }

    [Fact]
    public async Task Matching_unknown_invoices_is_404()
    {
        var response = await factory.CreateClient().GetAsync("/api/invoices/ap_missing/match");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
