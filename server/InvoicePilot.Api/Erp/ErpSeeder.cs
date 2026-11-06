using InvoicePilot.Api.Erp.Mongo;

namespace InvoicePilot.Api.Erp;

/// <summary>On startup: creates Mongo indexes and, if the ERP is empty, loads the demo data.</summary>
public sealed class ErpSeeder(IErpStore store, IConfiguration config, ILogger<ErpSeeder> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (store is MongoErpStore mongo)
        {
            await mongo.EnsureIndexesAsync(ct);
        }

        if (!config.GetValue("Erp:Seed", true)) return;

        if (await store.IsEmptyAsync(ct))
        {
            await store.UpsertAsync(ErpSeed.Create(), ct);
            log.LogInformation("Loaded demo ERP data");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
