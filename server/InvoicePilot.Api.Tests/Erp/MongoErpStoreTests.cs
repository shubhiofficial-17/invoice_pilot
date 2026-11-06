using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Erp.Mongo;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace InvoicePilot.Api.Tests.Erp;

/// <summary>Starts one real MongoDB container for the test class.</summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder().WithImage("mongo:7.0").Build();

    public IMongoClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Client = new MongoClient(_container.GetConnectionString());
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>A fresh database, so tests never see each other's data.</summary>
    public IMongoDatabase NewDatabase() => Client.GetDatabase($"test_{Guid.NewGuid():N}");
}

public sealed class MongoErpStoreTests(MongoFixture mongo) : ErpStoreContract, IClassFixture<MongoFixture>
{
    protected override async Task<IErpStore> CreateStoreAsync()
    {
        var store = new MongoErpStore(mongo.NewDatabase());
        await store.EnsureIndexesAsync();
        return store;
    }

    [Fact]
    public async Task Money_is_stored_as_decimal128_and_enums_as_strings()
    {
        var db = mongo.NewDatabase();
        var store = new MongoErpStore(db);
        await store.UpsertAsync(ErpSeed.Create());

        var raw = await db.GetCollection<MongoDB.Bson.BsonDocument>("purchaseOrders")
            .Find(MongoDB.Bson.BsonDocument.Parse("{ _id: 'PO-1001' }"))
            .FirstAsync();

        Assert.Equal(MongoDB.Bson.BsonType.Decimal128, raw["lines"][0]["unitPrice"].BsonType);
        Assert.Equal("Open", raw["status"].AsString);
        Assert.Equal("SUP-ACME", raw["supplierId"].AsString);
    }
}
