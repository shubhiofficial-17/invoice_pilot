using System.Net;
using System.Net.Http.Json;
using InvoicePilot.Api.Erp;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InvoicePilot.Api.Tests.Erp;

public class ErpEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<T?> GetAsync<T>(string url) => _client.GetFromJsonAsync<T>(url, TestJson.Options);

    [Fact]
    public async Task Demo_data_is_loaded_on_startup()
    {
        var suppliers = await GetAsync<List<Supplier>>("/api/erp/suppliers");

        Assert.NotNull(suppliers);
        Assert.Equal(ErpSeed.Create().Suppliers.Count, suppliers.Count);
        Assert.Contains(suppliers, s => s.Id == "SUP-OLD" && s.Status == SupplierStatus.Blocked);
    }

    [Fact]
    public async Task Purchase_order_is_returned_with_lines()
    {
        var po = await GetAsync<PurchaseOrder>("/api/erp/purchase-orders/po-1002");

        Assert.NotNull(po);
        Assert.Equal("PO-1002", po.Number);
        Assert.Equal(2, po.Lines.Count);
        Assert.Equal(PoStatus.Open, po.Status);
    }

    [Fact]
    public async Task Enums_and_dates_are_readable_in_json()
    {
        var json = await _client.GetStringAsync("/api/erp/purchase-orders/PO-1007");

        Assert.Contains("\"status\":\"cancelled\"", json);
        Assert.Contains("\"orderDate\":\"2026-10-05\"", json);
    }

    [Fact]
    public async Task Receipts_show_a_partial_delivery()
    {
        var receipts = await GetAsync<List<GoodsReceipt>>("/api/erp/purchase-orders/PO-1005/receipts");

        var receipt = Assert.Single(receipts!);
        Assert.Equal(300m, receipt.Lines.Single().QuantityReceived);
    }

    [Fact]
    public async Task Supplier_invoice_history_is_listed()
    {
        var invoices = await GetAsync<List<PostedInvoice>>("/api/erp/suppliers/SUP-NORTH/invoices");

        Assert.Contains(invoices!, i => i.InvoiceNumber == "NW-55012" && i.PoNumber == "PO-1003");
    }

    [Fact]
    public async Task Purchase_orders_can_be_filtered_by_supplier()
    {
        var orders = await GetAsync<List<PurchaseOrder>>("/api/erp/purchase-orders?supplierId=SUP-NORTH");

        Assert.Equal(new[] { "PO-1003", "PO-1008" }, orders!.Select(o => o.Number));
    }

    [Theory]
    [InlineData("/api/erp/suppliers/SUP-NOPE")]
    [InlineData("/api/erp/suppliers/SUP-NOPE/invoices")]
    [InlineData("/api/erp/purchase-orders/PO-9999")]
    [InlineData("/api/erp/purchase-orders/PO-9999/receipts")]
    public async Task Unknown_records_return_404_problem(string url)
    {
        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
