using InvoicePilot.Api.Invoices;
using InvoicePilot.Api.Invoices.Mongo;
using InvoicePilot.Api.Tests.Erp;

namespace InvoicePilot.Api.Tests.Invoices;

public abstract class InvoiceStoreContract
{
    protected abstract Task<IInvoiceStore> CreateStoreAsync();

    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    public static Invoice NewInvoice(string sha, DateTimeOffset? at = null, string? id = null) => new(
        id ?? InvoiceIds.New(),
        InvoiceStatus.Received,
        new InvoiceFile("inv.pdf", FileSniffer.Pdf, 4, sha),
        at ?? T0,
        at ?? T0,
        Version: 1);

    [Fact]
    public async Task Added_invoice_and_file_round_trip()
    {
        var store = await CreateStoreAsync();
        var invoice = NewInvoice("aaa");

        var (stored, created) = await store.AddAsync(invoice, [1, 2, 3, 4]);

        Assert.True(created);
        Assert.Equal(invoice, stored);
        Assert.Equal(invoice, await store.GetAsync(invoice.Id));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await store.GetFileAsync(invoice.Id));
    }

    [Fact]
    public async Task Same_file_twice_returns_the_first_invoice()
    {
        var store = await CreateStoreAsync();
        var first = NewInvoice("same");
        await store.AddAsync(first, [1]);

        var (stored, created) = await store.AddAsync(NewInvoice("same"), [1]);

        Assert.False(created);
        Assert.Equal(first.Id, stored.Id);
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task Unknown_id_returns_null()
    {
        var store = await CreateStoreAsync();

        Assert.Null(await store.GetAsync("ap_missing"));
        Assert.Null(await store.GetFileAsync("ap_missing"));
    }

    [Fact]
    public async Task List_is_newest_first_and_respects_the_limit()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewInvoice("1", T0), [1]);
        await store.AddAsync(NewInvoice("2", T0.AddMinutes(2), id: "ap_newest"), [2]);
        await store.AddAsync(NewInvoice("3", T0.AddMinutes(1)), [3]);

        var all = await store.ListAsync();
        var top = await store.ListAsync(limit: 1);

        Assert.Equal(new[] { "2", "3", "1" }, all.Select(i => i.File.Sha256));
        Assert.Equal("ap_newest", Assert.Single(top).Id);
    }

    [Fact]
    public async Task Update_succeeds_once_per_version()
    {
        var store = await CreateStoreAsync();
        var invoice = NewInvoice("v");
        await store.AddAsync(invoice, [1]);

        var saved = await store.TryUpdateAsync(invoice with { Status = InvoiceStatus.Extracting });
        var stale = await store.TryUpdateAsync(invoice with { Status = InvoiceStatus.ExtractionFailed });

        Assert.NotNull(saved);
        Assert.Equal(2, saved.Version);
        Assert.Null(stale);
        Assert.Equal(InvoiceStatus.Extracting, (await store.GetAsync(invoice.Id))!.Status);
    }

    [Fact]
    public async Task Update_of_a_missing_invoice_returns_null()
    {
        var store = await CreateStoreAsync();

        Assert.Null(await store.TryUpdateAsync(NewInvoice("x")));
    }

    [Fact]
    public async Task Invoices_can_be_listed_by_status_oldest_first()
    {
        var store = await CreateStoreAsync();
        var older = NewInvoice("o", T0);
        var newer = NewInvoice("n", T0.AddMinutes(5));
        await store.AddAsync(newer, [1]);
        await store.AddAsync(older, [2]);

        var received = await store.ListByStatusAsync(InvoiceStatus.Received);

        Assert.Equal(new[] { older.Id, newer.Id }, received.Select(i => i.Id));
        Assert.Empty(await store.ListByStatusAsync(InvoiceStatus.Extracted));
    }
}

public sealed class InMemoryInvoiceStoreTests : InvoiceStoreContract
{
    protected override Task<IInvoiceStore> CreateStoreAsync() => Task.FromResult<IInvoiceStore>(new InMemoryInvoiceStore());
}

public sealed class MongoInvoiceStoreTests(MongoFixture mongo) : InvoiceStoreContract, IClassFixture<MongoFixture>
{
    protected override async Task<IInvoiceStore> CreateStoreAsync()
    {
        var store = new MongoInvoiceStore(mongo.NewDatabase());
        await store.EnsureIndexesAsync();
        return store;
    }

    [Fact]
    public async Task Concurrent_uploads_of_the_same_file_create_one_invoice()
    {
        var store = (MongoInvoiceStore)await CreateStoreAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => store.AddAsync(NewInvoice("race"), [9, 9])));

        Assert.Single(results, r => r.Created);
        Assert.Single(results.Select(r => r.Invoice.Id).Distinct());
        Assert.Single(await store.ListAsync());
    }
}
