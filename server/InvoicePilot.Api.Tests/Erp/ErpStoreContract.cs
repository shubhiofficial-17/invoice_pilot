using InvoicePilot.Api.Erp;

namespace InvoicePilot.Api.Tests.Erp;

/// <summary>
/// One suite, run against every IErpStore implementation, so the in-memory store
/// used in tests can't drift from the MongoDB store used in production.
/// </summary>
public abstract class ErpStoreContract
{
    /// <summary>Returns a new, empty store.</summary>
    protected abstract Task<IErpStore> CreateStoreAsync();

    private async Task<IErpStore> SeededAsync()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(ErpSeed.Create());
        return store;
    }

    [Fact]
    public async Task New_store_is_empty_until_data_is_loaded()
    {
        var store = await CreateStoreAsync();
        Assert.True(await store.IsEmptyAsync());

        await store.UpsertAsync(ErpSeed.Create());

        Assert.False(await store.IsEmptyAsync());
    }

    [Fact]
    public async Task Suppliers_round_trip_sorted_by_id()
    {
        var store = await SeededAsync();
        var expected = ErpSeed.Create().Suppliers.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();

        var suppliers = await store.ListSuppliersAsync();

        Assert.Equivalent(expected, suppliers, strict: true);
        Assert.Equal(expected.Select(s => s.Id), suppliers.Select(s => s.Id));
    }

    [Fact]
    public async Task Purchase_order_round_trips_with_lines_and_exact_decimals()
    {
        var store = await SeededAsync();
        var expected = ErpSeed.Create().PurchaseOrders.Single(p => p.Number == "PO-1001");

        var po = await store.GetPurchaseOrderAsync("PO-1001");

        Assert.NotNull(po);
        Assert.Equivalent(expected, po, strict: true);
        Assert.Equal(4.50m, po.Lines[0].UnitPrice);
        Assert.Equal(0.18m, po.Lines[0].TaxRate);
        Assert.Equal(new DateOnly(2026, 10, 1), po.OrderDate);
    }

    [Fact]
    public async Task Lookups_ignore_case_and_spaces()
    {
        var store = await SeededAsync();

        Assert.NotNull(await store.GetPurchaseOrderAsync("  po-1001 "));
        Assert.NotNull(await store.GetSupplierAsync("sup-acme"));
        Assert.Equal(2, (await store.ListReceiptsForPoAsync("po-1003")).Count);
        Assert.NotEmpty(await store.ListPostedInvoicesAsync("sup-north"));
    }

    [Fact]
    public async Task Unknown_keys_return_null_or_empty()
    {
        var store = await SeededAsync();

        Assert.Null(await store.GetSupplierAsync("SUP-NOPE"));
        Assert.Null(await store.GetPurchaseOrderAsync("PO-9999"));
        Assert.Empty(await store.ListReceiptsForPoAsync("PO-9999"));
        Assert.Empty(await store.ListPostedInvoicesAsync("SUP-NOPE"));
        Assert.Empty(await store.ListPurchaseOrdersAsync("SUP-NOPE"));
    }

    [Fact]
    public async Task Purchase_orders_filter_by_supplier()
    {
        var store = await SeededAsync();

        var orders = await store.ListPurchaseOrdersAsync("SUP-ACME");

        Assert.Equal(new[] { "PO-1001", "PO-1005" }, orders.Select(o => o.Number));
        Assert.Equal(8, (await store.ListPurchaseOrdersAsync()).Count);
    }

    [Fact]
    public async Task Receipts_come_oldest_first()
    {
        var store = await SeededAsync();

        var receipts = await store.ListReceiptsForPoAsync("PO-1003");

        Assert.Equal(new[] { "GRN-5003", "GRN-5006" }, receipts.Select(r => r.Number));
        Assert.Equal(200m, receipts.Sum(r => r.Lines.Sum(l => l.QuantityReceived)));
    }

    [Fact]
    public async Task Posted_invoices_come_newest_first()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(new ErpData([], [], [],
        [
            new("SUP-A", "OLD-1", new DateOnly(2026, 1, 1), null, "INR", 10m, PostedInvoiceStatus.Paid),
            new("SUP-A", "NEW-1", new DateOnly(2026, 6, 1), "PO-1", "INR", 20m, PostedInvoiceStatus.Posted),
            new("SUP-B", "OTHER", new DateOnly(2026, 3, 1), null, "INR", 30m, PostedInvoiceStatus.Paid),
        ]));

        var invoices = await store.ListPostedInvoicesAsync("SUP-A");

        Assert.Equal(new[] { "NEW-1", "OLD-1" }, invoices.Select(i => i.InvoiceNumber));
        Assert.Null(invoices[1].PoNumber);
    }

    [Fact]
    public async Task Upsert_replaces_existing_records_instead_of_duplicating()
    {
        var store = await SeededAsync();
        var seed = ErpSeed.Create();
        var renamed = seed.Suppliers[0] with { Name = "Acme Renamed" };

        await store.UpsertAsync(seed with { Suppliers = [renamed] });

        Assert.Equal(seed.Suppliers.Count, (await store.ListSuppliersAsync()).Count);
        Assert.Equal("Acme Renamed", (await store.GetSupplierAsync(renamed.Id))!.Name);
        Assert.Equal(seed.PostedInvoices.Count(i => i.SupplierId == "SUP-NORTH"),
            (await store.ListPostedInvoicesAsync("SUP-NORTH")).Count);
    }

    [Fact]
    public async Task Booking_the_same_invoice_number_twice_is_refused()
    {
        var store = await SeededAsync();
        var invoice = new PostedInvoice("SUP-ACME", "AC/2026/0420", new DateOnly(2026, 11, 2), "PO-1001", "INR", 6726.00m, PostedInvoiceStatus.Posted);

        Assert.True(await store.AddPostedInvoiceAsync(invoice));
        Assert.False(await store.AddPostedInvoiceAsync(invoice with { InvoiceNumber = " ac/2026/0420 ", Total = 1m }));

        var booked = (await store.ListPostedInvoicesAsync("SUP-ACME")).Single(i => i.InvoiceNumber == "AC/2026/0420");
        Assert.Equal(6726.00m, booked.Total);
    }

    [Fact]
    public async Task Same_invoice_number_from_different_suppliers_is_allowed()
    {
        var store = await SeededAsync();
        var a = new PostedInvoice("SUP-ACME", "INV-1", new DateOnly(2026, 11, 1), null, "INR", 1m, PostedInvoiceStatus.Posted);

        Assert.True(await store.AddPostedInvoiceAsync(a));
        Assert.True(await store.AddPostedInvoiceAsync(a with { SupplierId = "SUP-ZEN" }));
    }
}

public sealed class InMemoryErpStoreTests : ErpStoreContract
{
    protected override Task<IErpStore> CreateStoreAsync() => Task.FromResult<IErpStore>(new InMemoryErpStore());
}
