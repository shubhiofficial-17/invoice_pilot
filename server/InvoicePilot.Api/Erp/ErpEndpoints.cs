namespace InvoicePilot.Api.Erp;

/// <summary>Read-only views of the mock ERP, for the UI and for debugging the agent's tools.</summary>
public static class ErpEndpoints
{
    public static IEndpointRouteBuilder MapErpEndpoints(this IEndpointRouteBuilder app)
    {
        var erp = app.MapGroup("/api/erp").WithTags("ERP");

        erp.MapGet("/suppliers", async (IErpStore store, CancellationToken ct) =>
            Results.Ok(await store.ListSuppliersAsync(ct)));

        erp.MapGet("/suppliers/{id}", async (string id, IErpStore store, CancellationToken ct) =>
            await store.GetSupplierAsync(id, ct) is { } supplier ? Results.Ok(supplier) : Results.NotFound());

        erp.MapGet("/suppliers/{id}/invoices", async (string id, IErpStore store, CancellationToken ct) =>
            await store.GetSupplierAsync(id, ct) is null
                ? Results.NotFound()
                : Results.Ok(await store.ListPostedInvoicesAsync(id, ct)));

        erp.MapGet("/purchase-orders", async (string? supplierId, IErpStore store, CancellationToken ct) =>
            Results.Ok(await store.ListPurchaseOrdersAsync(supplierId, ct)));

        erp.MapGet("/purchase-orders/{number}", async (string number, IErpStore store, CancellationToken ct) =>
            await store.GetPurchaseOrderAsync(number, ct) is { } po ? Results.Ok(po) : Results.NotFound());

        erp.MapGet("/purchase-orders/{number}/receipts", async (string number, IErpStore store, CancellationToken ct) =>
            await store.GetPurchaseOrderAsync(number, ct) is null
                ? Results.NotFound()
                : Results.Ok(await store.ListReceiptsForPoAsync(number, ct)));

        return app;
    }
}
