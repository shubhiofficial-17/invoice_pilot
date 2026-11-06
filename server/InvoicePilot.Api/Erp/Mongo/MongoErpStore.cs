using MongoDB.Driver;

namespace InvoicePilot.Api.Erp.Mongo;

public sealed class MongoErpStore : IErpStore
{
    private readonly IMongoCollection<SupplierDoc> _suppliers;
    private readonly IMongoCollection<PurchaseOrderDoc> _orders;
    private readonly IMongoCollection<GoodsReceiptDoc> _receipts;
    private readonly IMongoCollection<PostedInvoiceDoc> _invoices;

    public MongoErpStore(IMongoDatabase db)
    {
        MongoSetup.Configure();
        _suppliers = db.GetCollection<SupplierDoc>("suppliers");
        _orders = db.GetCollection<PurchaseOrderDoc>("purchaseOrders");
        _receipts = db.GetCollection<GoodsReceiptDoc>("goodsReceipts");
        _invoices = db.GetCollection<PostedInvoiceDoc>("postedInvoices");
    }

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await _orders.Indexes.CreateOneAsync(
            new CreateIndexModel<PurchaseOrderDoc>(Builders<PurchaseOrderDoc>.IndexKeys.Ascending(o => o.SupplierId)),
            cancellationToken: ct);
        await _receipts.Indexes.CreateOneAsync(
            new CreateIndexModel<GoodsReceiptDoc>(Builders<GoodsReceiptDoc>.IndexKeys.Ascending(r => r.PoNumber)),
            cancellationToken: ct);
        await _invoices.Indexes.CreateOneAsync(
            new CreateIndexModel<PostedInvoiceDoc>(Builders<PostedInvoiceDoc>.IndexKeys
                .Ascending(i => i.SupplierId)
                .Descending(i => i.InvoiceDate)),
            cancellationToken: ct);
    }

    public async Task<IReadOnlyList<Supplier>> ListSuppliersAsync(CancellationToken ct = default)
    {
        var docs = await _suppliers.Find(FilterDefinition<SupplierDoc>.Empty)
            .SortBy(s => s.Id)
            .ToListAsync(ct);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public async Task<Supplier?> GetSupplierAsync(string id, CancellationToken ct = default)
    {
        var key = ErpKeys.Normalize(id);
        var doc = await _suppliers.Find(s => s.Id == key).FirstOrDefaultAsync(ct);
        return doc?.ToModel();
    }

    public async Task<IReadOnlyList<PurchaseOrder>> ListPurchaseOrdersAsync(string? supplierId = null, CancellationToken ct = default)
    {
        var filter = supplierId is null
            ? FilterDefinition<PurchaseOrderDoc>.Empty
            : Builders<PurchaseOrderDoc>.Filter.Eq(o => o.SupplierId, ErpKeys.Normalize(supplierId));
        var docs = await _orders.Find(filter).SortBy(o => o.Number).ToListAsync(ct);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public async Task<PurchaseOrder?> GetPurchaseOrderAsync(string number, CancellationToken ct = default)
    {
        var key = ErpKeys.Normalize(number);
        var doc = await _orders.Find(o => o.Number == key).FirstOrDefaultAsync(ct);
        return doc?.ToModel();
    }

    public async Task<IReadOnlyList<GoodsReceipt>> ListReceiptsForPoAsync(string poNumber, CancellationToken ct = default)
    {
        var po = ErpKeys.Normalize(poNumber);
        var docs = await _receipts.Find(r => r.PoNumber == po)
            .SortBy(r => r.ReceivedOn)
            .ThenBy(r => r.Number)
            .ToListAsync(ct);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<PostedInvoice>> ListPostedInvoicesAsync(string supplierId, CancellationToken ct = default)
    {
        var supplier = ErpKeys.Normalize(supplierId);
        var docs = await _invoices.Find(i => i.SupplierId == supplier)
            .SortByDescending(i => i.InvoiceDate)
            .ThenBy(i => i.InvoiceNumber)
            .ToListAsync(ct);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public async Task<bool> AddPostedInvoiceAsync(PostedInvoice invoice, CancellationToken ct = default)
    {
        try
        {
            await _invoices.InsertOneAsync(PostedInvoiceDoc.From(ErpNormalizer.Normalize(invoice)), cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task UpsertAsync(ErpData data, CancellationToken ct = default)
    {
        await ReplaceAllAsync(_suppliers,
            data.Suppliers.Select(s => SupplierDoc.From(ErpNormalizer.Normalize(s))), d => d.Id, ct);
        await ReplaceAllAsync(_orders,
            data.PurchaseOrders.Select(o => PurchaseOrderDoc.From(ErpNormalizer.Normalize(o))), d => d.Number, ct);
        await ReplaceAllAsync(_receipts,
            data.Receipts.Select(r => GoodsReceiptDoc.From(ErpNormalizer.Normalize(r))), d => d.Number, ct);
        await ReplaceAllAsync(_invoices,
            data.PostedInvoices.Select(i => PostedInvoiceDoc.From(ErpNormalizer.Normalize(i))), d => d.Key, ct);
    }

    public async Task<bool> IsEmptyAsync(CancellationToken ct = default)
    {
        var suppliers = await _suppliers.CountDocumentsAsync(FilterDefinition<SupplierDoc>.Empty, new CountOptions { Limit = 1 }, ct);
        var orders = await _orders.CountDocumentsAsync(FilterDefinition<PurchaseOrderDoc>.Empty, new CountOptions { Limit = 1 }, ct);
        return suppliers == 0 && orders == 0;
    }

    private static async Task ReplaceAllAsync<T>(
        IMongoCollection<T> collection, IEnumerable<T> docs, Func<T, string> key, CancellationToken ct)
    {
        var models = docs
            .Select(d => new ReplaceOneModel<T>(Builders<T>.Filter.Eq("_id", key(d)), d) { IsUpsert = true })
            .ToList();
        if (models.Count == 0) return; // BulkWrite rejects an empty batch.
        await collection.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false }, ct);
    }
}
