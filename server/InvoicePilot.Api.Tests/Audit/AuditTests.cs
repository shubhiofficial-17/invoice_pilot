using System.Net.Http.Json;
using System.Text.Json;
using InvoicePilot.Api.Audit;
using InvoicePilot.Api.Invoices;
using InvoicePilot.Api.Tests.Erp;
using InvoicePilot.Api.Tests.Extraction;
using MongoDB.Driver;
using static InvoicePilot.Api.Tests.AgentScript;

namespace InvoicePilot.Api.Tests.Audit;

public abstract class AuditLogContract
{
    protected abstract IAuditLog Create();

    [Fact]
    public async Task Events_are_numbered_and_chained()
    {
        var log = Create();

        var a = await log.AppendAsync("ap_1", "asha", "invoice.received", "Uploaded a.pdf", AuditData.Of(("size", 10)));
        var b = await log.AppendAsync("ap_1", "system", "extraction.completed", "Read it");
        await log.AppendAsync("ap_2", "asha", "invoice.received", "Another invoice");

        Assert.Equal((1, AuditChain.Genesis), (a.Seq, a.PrevHash));
        Assert.Equal((2, a.Hash), (b.Seq, b.PrevHash));
        var events = await log.ListAsync("ap_1");
        Assert.Equal(new[] { 1, 2 }, events.Select(e => e.Seq));
        Assert.Equal("10", events[0].Data["size"]);
        Assert.Equal(new AuditVerification(true, null, 2), AuditChain.Verify(events));
    }

    [Fact]
    public async Task An_unknown_invoice_has_an_empty_intact_trail()
    {
        var events = await Create().ListAsync("ap_none");

        Assert.Empty(events);
        Assert.True(AuditChain.Verify(events).Intact);
    }
}

public sealed class InMemoryAuditLogTests : AuditLogContract
{
    protected override IAuditLog Create() => new InMemoryAuditLog(TimeProvider.System);

    [Fact]
    public async Task Editing_an_old_entry_is_detected()
    {
        var log = new InMemoryAuditLog(TimeProvider.System);
        for (var i = 0; i < 4; i++) await log.AppendAsync("ap_1", "asha", "t", $"event {i}");

        log.Tamper("ap_1", 2, e => e with { Summary = "nothing to see here" });

        Assert.Equal(new AuditVerification(false, 2, 4), AuditChain.Verify(await log.ListAsync("ap_1")));
    }

    [Fact]
    public async Task Rehashing_an_edited_entry_still_breaks_the_next_link()
    {
        var log = new InMemoryAuditLog(TimeProvider.System);
        for (var i = 0; i < 3; i++) await log.AppendAsync("ap_1", "asha", "t", $"event {i}");

        log.Tamper("ap_1", 1, e => e with
        {
            Actor = "mallory",
            Hash = AuditChain.HashOf(e.InvoiceId, e.Seq, e.At, "mallory", e.Type, e.Summary, e.Data, e.PrevHash),
        });

        Assert.Equal(2, AuditChain.Verify(await log.ListAsync("ap_1")).BrokenAtSeq);
    }

    [Fact]
    public async Task Long_values_are_capped()
    {
        var e = await new InMemoryAuditLog(TimeProvider.System).AppendAsync("ap_1", "a", "t", "s", AuditData.Of(("big", new string('x', 5000))));

        Assert.True(e.Data["big"].Length <= 1001);
    }
}

public sealed class MongoAuditLogTests(MongoFixture mongo) : AuditLogContract, IClassFixture<MongoFixture>
{
    private MongoAuditLog New()
    {
        var log = new MongoAuditLog(mongo.NewDatabase(), TimeProvider.System);
        log.EnsureIndexesAsync().GetAwaiter().GetResult();
        return log;
    }

    protected override IAuditLog Create() => New();

    [Fact]
    public async Task Concurrent_appends_keep_one_unbroken_chain()
    {
        var log = New();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => log.AppendAsync("ap_race", "w" + i, "t", $"event {i}")));

        var events = await log.ListAsync("ap_race");
        Assert.Equal(Enumerable.Range(1, 20), events.Select(e => e.Seq));
        Assert.True(AuditChain.Verify(events).Intact);
    }

    [Fact]
    public async Task Editing_a_stored_document_is_detected()
    {
        var log = New();
        for (var i = 0; i < 3; i++) await log.AppendAsync("ap_1", "asha", "t", $"event {i}");

        await log.Collection.UpdateOneAsync(e => e.InvoiceId == "ap_1" && e.Seq == 2,
            Builders<AuditEventDoc>.Update.Set(e => e.Summary, "edited"));

        Assert.Equal(2, AuditChain.Verify(await log.ListAsync("ap_1")).BrokenAtSeq);
    }
}

public class AuditTrailEndpointTests(AiAppFactory factory) : IClassFixture<AiAppFactory>
{
    [Fact]
    public async Task The_whole_story_of_an_approved_invoice_is_in_the_trail()
    {
        var number = "AC/2026/" + Random.Shared.Next(10_000, 99_999);
        factory.Llm.Handler = Steps(SampleReplies.AcmeJson.Replace("AC/2026/0420", number), Standard("approve"));
        var client = factory.CreateClient();
        var uploaded = await client.UploadAsync(TestPdfs.WithLines("Audit me", Guid.NewGuid().ToString()));
        var invoice = await client.WaitUntilSettledAsync(uploaded.Id);
        await client.PostAsJsonAsync($"/api/invoices/{invoice.Id}/actions",
            new { action = "confirm", expectedVersion = invoice.Version }, TestJson.Options);

        var trail = await client.GetFromJsonAsync<JsonElement>($"/api/invoices/{invoice.Id}/audit");

        var types = trail.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.Equal(new[]
        {
            "invoice.received", "extraction.completed",
            "agent.tool_call", "agent.tool_call", "agent.tool_call", "agent.tool_call",
            "agent.proposed", "decision.recommended", "human.confirm", "erp.booked",
        }, types);
        Assert.True(trail.GetProperty("verification").GetProperty("intact").GetBoolean());
        Assert.Equal("clerk", trail.GetProperty("events")[8].GetProperty("actor").GetString());
    }

    [Fact]
    public async Task A_policy_override_is_its_own_event()
    {
        var gloves = SampleReplies.AcmeJson.Replace("AC/2026/0420", "AC/2026/" + Random.Shared.Next(10_000, 99_999))
            .Replace("\"quantity\": 1000, \"unitPrice\": 4.50", "\"quantity\": 2000, \"unitPrice\": 4.50")
            .Replace("\"amount\": 4500.00", "\"amount\": 9000.00").Replace("\"subtotal\": 5700.00", "\"subtotal\": 10200.00")
            .Replace("\"taxTotal\": 1026.00", "\"taxTotal\": 1836.00").Replace("\"total\": 6726.00", "\"total\": 12036.00");
        factory.Llm.Handler = Steps(gloves, Standard("approve"));
        var client = factory.CreateClient();
        var uploaded = await client.UploadAsync(TestPdfs.WithLines("Override me", Guid.NewGuid().ToString()));
        await client.WaitUntilSettledAsync(uploaded.Id);

        var trail = await client.GetFromJsonAsync<JsonElement>($"/api/invoices/{uploaded.Id}/audit");

        var over = trail.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("type").GetString() == "policy.override");
        Assert.Equal("policy", over.GetProperty("actor").GetString());
        Assert.Equal("Approve", over.GetProperty("data").GetProperty("agentOutcome").GetString());
    }
}
