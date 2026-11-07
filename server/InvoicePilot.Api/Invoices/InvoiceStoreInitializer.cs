using InvoicePilot.Api.Invoices.Mongo;

namespace InvoicePilot.Api.Invoices;

/// <summary>Creates the invoice indexes (including the unique file hash) on startup.</summary>
public sealed class InvoiceStoreInitializer(IInvoiceStore store) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (store is MongoInvoiceStore mongo) await mongo.EnsureIndexesAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
