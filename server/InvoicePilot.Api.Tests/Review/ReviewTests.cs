using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvoicePilot.Api.Agent;
using InvoicePilot.Api.Ai;
using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Extraction;
using InvoicePilot.Api.Invoices;
using InvoicePilot.Api.Matching;
using InvoicePilot.Api.Policy;
using InvoicePilot.Api.Review;
using InvoicePilot.Api.Tests.Agent;
using InvoicePilot.Api.Tests.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static InvoicePilot.Api.Tests.AgentScript;
using static InvoicePilot.Api.Tests.Matching.MatchEngineTests;

namespace InvoicePilot.Api.Tests.Review;

public class ReviewServiceTests
{
    private readonly InMemoryInvoiceStore _store = new();
    private readonly InMemoryErpStore _erp = new();
    private readonly ReviewService _review;

    public ReviewServiceTests()
    {
        _erp.UpsertAsync(ErpSeed.Create()).GetAwaiter().GetResult();
        _review = new ReviewService(_store, _erp, Options.Create(new ApPolicyOptions()), TimeProvider.System);
    }

    /// <summary>An invoice waiting for review, with a real match and a real guardrail decision.</summary>
    private async Task<Invoice> WaitingAsync(string po, Outcome agentProposes = Outcome.Approve,
        Func<ExtractedInvoice, ExtractedInvoice>? tweak = null, string? number = null)
    {
        var data = Billing(po, invoiceNumber: number ?? "T-" + Guid.NewGuid().ToString("N")[..8]);
        data = tweak?.Invoke(data) ?? data;
        var read = InvoiceAgentTests.ReadInvoice(data);
        var match = await new MatchService(_erp).MatchAsync(read.Extraction!);
        var decision = await new DecisionService(_erp, Options.Create(new ApPolicyOptions()))
            .DecideAsync(match, new AgentProposal(agentProposes, "agent reason", [], null));
        var invoice = read with
        {
            Id = InvoiceIds.New(),
            Status = InvoiceStatus.AwaitingReview,
            File = read.File with { Sha256 = Guid.NewGuid().ToString() },
            Version = 1,
            Match = match,
            Decision = decision,
        };
        await _store.AddAsync(invoice, [1]);
        return invoice;
    }

    private Task<ReviewResult> Act(Invoice i, string action, Outcome? outcome = null, string? note = null, string actor = "asha")
        => _review.ActAsync(i.Id, new ReviewRequest(action, i.Version, outcome, note), actor);

    [Fact]
    public async Task Confirming_an_approval_books_the_invoice_in_the_erp()
    {
        var invoice = await WaitingAsync("PO-1001");

        var result = await Act(invoice, "confirm");

        Assert.Equal(200, result.Status);
        Assert.Equal(InvoiceStatus.Approved, result.Invoice!.Status);
        Assert.NotNull(result.Invoice.Booking);
        var action = Assert.Single(result.Invoice.Actions!);
        Assert.Equal(("asha", "confirm", Outcome.Approve), (action.Actor, action.Action, action.Outcome));
        var booked = (await _erp.ListPostedInvoicesAsync("SUP-ACME")).Single(p => p.InvoiceNumber == invoice.Extraction!.Data.InvoiceNumber);
        Assert.Equal(invoice.Id, booked.SourceRef);
        Assert.Equal(invoice.Extraction!.Data.Total, booked.Total);
    }

    [Fact]
    public async Task A_stale_view_or_a_second_click_is_a_conflict()
    {
        var invoice = await WaitingAsync("PO-1001");

        Assert.Equal(409, (await _review.ActAsync(invoice.Id, new ReviewRequest("confirm", invoice.Version + 5), "a")).Status);
        Assert.Equal(200, (await Act(invoice, "confirm")).Status);
        Assert.Equal(409, (await Act(invoice, "confirm")).Status); // same (now old) version again
        Assert.Single(await _erp.ListPostedInvoicesAsync("SUP-ACME"), p => p.SourceRef == invoice.Id);
    }

    [Fact]
    public async Task Confirming_a_hold_queues_the_supplier_email_with_the_status_change()
    {
        var invoice = await WaitingAsync("PO-1005"); // agent approved; policy holds

        var result = await Act(invoice, "confirm");

        Assert.Equal(InvoiceStatus.OnHold, result.Invoice!.Status);
        Assert.Equal("ar@acme-industrial.example", result.Invoice.Outbox!.To);
        Assert.Contains("only 300 received", result.Invoice.Outbox.Body);
        Assert.Null(result.Invoice.Outbox.SentAt);
        Assert.DoesNotContain(await _erp.ListPostedInvoicesAsync("SUP-ACME"), p => p.SourceRef == invoice.Id);
    }

    [Fact]
    public async Task A_reviewer_cannot_loosen_what_the_policy_requires()
    {
        var invoice = await WaitingAsync("PO-1005");

        var result = await Act(invoice, "override", Outcome.Approve, "Supplier promised the rest tomorrow.");

        Assert.Equal(422, result.Status);
        Assert.Contains("stricter, not looser", result.Detail);
    }

    [Fact]
    public async Task An_override_needs_an_outcome_and_a_note()
    {
        var invoice = await WaitingAsync("PO-1001");

        Assert.Equal(422, (await Act(invoice, "override", Outcome.Reject)).Status);
        Assert.Equal(422, (await Act(invoice, "override", null, "a good reason")).Status);
        Assert.Equal(422, (await Act(invoice, "wave_through")).Status);
    }

    [Fact]
    public async Task A_reviewer_can_make_it_stricter()
    {
        var invoice = await WaitingAsync("PO-1001");

        var result = await Act(invoice, "override", Outcome.Reject, "We never ordered from this branch.");

        Assert.Equal(InvoiceStatus.Rejected, result.Invoice!.Status);
        Assert.Equal("We never ordered from this branch.", result.Invoice.Actions!.Single().Note);
    }

    [Fact]
    public async Task A_routed_invoice_waits_for_the_approver_who_can_approve_it()
    {
        var invoice = await WaitingAsync("PO-1001", tweak: d => d with
        {
            Lines = d.Lines.Select((l, i) => i == 0 ? l with { UnitPrice = 4.95m, Amount = 4950m } : l).ToList(),
            Subtotal = 6150m, TaxTotal = 1107m, Total = 7257m,
        });
        Assert.Equal(Outcome.RouteForApproval, invoice.Decision!.Outcome);
        Assert.Equal(ErpSeed.Buyer, invoice.Decision.Approver!.Email);

        var routed = (await Act(invoice, "confirm")).Invoice!;
        Assert.Equal(InvoiceStatus.AwaitingApproval, routed.Status);
        Assert.Equal(422, (await Act(routed, "confirm")).Status); // the approver's actions are approve/reject

        var approved = await Act(routed, "approve", note: "Steel prices went up; accepted.", actor: "priya");
        Assert.Equal(InvoiceStatus.Approved, approved.Invoice!.Status);
        Assert.Equal(new[] { "asha", "priya" }, approved.Invoice.Actions!.Select(a => a.Actor));
    }

    [Fact]
    public async Task An_approver_rejection_needs_a_note()
    {
        var invoice = await WaitingAsync("PO-1001", agentProposes: Outcome.RouteForApproval);
        var routed = (await Act(invoice, "confirm")).Invoice!;

        Assert.Equal(422, (await Act(routed, "reject")).Status);
        Assert.Equal(InvoiceStatus.Rejected, (await Act(routed, "reject", note: "Not our order.")).Invoice!.Status);
    }

    [Fact]
    public async Task A_duplicate_can_only_be_rejected()
    {
        var invoice = await WaitingAsync("PO-1003", number: "NW-55012");

        Assert.Equal(Outcome.Reject, invoice.Decision!.Outcome);
        Assert.Equal(422, (await Act(invoice, "override", Outcome.RouteForApproval, "please check")).Status);
        Assert.Equal(InvoiceStatus.Rejected, (await Act(invoice, "confirm")).Invoice!.Status);
    }

    [Fact]
    public async Task If_someone_else_booked_the_same_invoice_it_comes_back_for_review()
    {
        var invoice = await WaitingAsync("PO-1001");
        var data = invoice.Extraction!.Data;
        await _erp.AddPostedInvoiceAsync(new PostedInvoice("SUP-ACME", data.InvoiceNumber!, data.InvoiceDate!.Value, "PO-1001",
            "INR", data.Total!.Value, PostedInvoiceStatus.Posted, "ap_someone_else"));

        var result = await Act(invoice, "confirm");

        Assert.Equal(409, result.Status);
        var reloaded = (await _store.GetAsync(invoice.Id))!;
        Assert.Equal(InvoiceStatus.AwaitingReview, reloaded.Status);
        Assert.Contains("booked by someone else", reloaded.FailureReason);
    }

    [Fact]
    public async Task Nothing_to_decide_on_an_invoice_that_is_not_waiting()
    {
        var invoice = await WaitingAsync("PO-1001");
        var rejected = (await Act(invoice, "override", Outcome.Reject, "not ours at all")).Invoice!;

        Assert.Equal(409, (await Act(rejected, "confirm")).Status);
        Assert.Equal(404, (await _review.ActAsync("ap_missing", new ReviewRequest("confirm", 1), "a")).Status);
    }

    [Fact]
    public async Task The_reconciler_finishes_an_interrupted_booking_once()
    {
        var invoice = await WaitingAsync("PO-1001");
        // Simulate a crash after the claim: approved, but never booked.
        await _store.TryUpdateAsync(invoice with { Status = InvoiceStatus.Approved, Booking = "pending" });
        var services = new ServiceCollection()
            .AddSingleton<IInvoiceStore>(_store)
            .AddSingleton<IErpStore>(_erp)
            .AddSingleton(Options.Create(new ApPolicyOptions()))
            .AddSingleton(TimeProvider.System)
            .AddTransient<ReviewService>()
            .BuildServiceProvider();
        var reconciler = new BookingReconciler(_store, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<BookingReconciler>.Instance);

        await reconciler.StartAsync(default);
        await reconciler.StartAsync(default);

        Assert.Single(await _erp.ListPostedInvoicesAsync("SUP-ACME"), p => p.SourceRef == invoice.Id);
    }
}

public class ReviewEndpointTests(AiAppFactory factory) : IClassFixture<AiAppFactory>
{
    private static string Acme(string number) => SampleReplies.AcmeJson.Replace("AC/2026/0420", number);

    private async Task<Invoice> SettledAsync(HttpClient client, string reply, string outcome = "approve")
    {
        factory.Llm.Handler = Steps(reply, Standard(outcome));
        var uploaded = await client.UploadAsync(TestPdfs.WithLines("Review me", Guid.NewGuid().ToString()));
        return await client.WaitUntilSettledAsync(uploaded.Id);
    }

    [Fact]
    public async Task A_person_confirms_and_the_invoice_is_booked()
    {
        var client = factory.CreateClient();
        var number = "AC/2026/" + Random.Shared.Next(10_000, 99_999);
        var invoice = await SettledAsync(client, Acme(number));
        Assert.Equal(InvoiceStatus.AwaitingReview, invoice.Status);

        var response = await client.PostAsJsonAsync($"/api/invoices/{invoice.Id}/actions",
            new { action = "confirm", expectedVersion = invoice.Version }, TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approved = await response.Content.ReadFromJsonAsync<Invoice>(TestJson.Options);
        Assert.Equal(InvoiceStatus.Approved, approved!.Status);
        var erp = await client.GetFromJsonAsync<List<PostedInvoice>>("/api/erp/suppliers/SUP-ACME/invoices", TestJson.Options);
        Assert.Contains(erp!, p => p.InvoiceNumber == number && p.SourceRef == invoice.Id);
    }

    [Fact]
    public async Task Rule_violations_come_back_as_problem_details()
    {
        var client = factory.CreateClient();
        var invoice = await SettledAsync(client, Acme("AC/2026/" + Random.Shared.Next(10_000, 99_999)));

        var response = await client.PostAsJsonAsync($"/api/invoices/{invoice.Id}/actions",
            new { action = "override", outcome = "reject", expectedVersion = invoice.Version }, TestJson.Options);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Note required", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Held_invoices_appear_in_the_outbox()
    {
        var client = factory.CreateClient();
        var gloves = Acme("AC/2026/" + Random.Shared.Next(10_000, 99_999))
            .Replace("\"quantity\": 1000, \"unitPrice\": 4.50", "\"quantity\": 2000, \"unitPrice\": 4.50")
            .Replace("\"amount\": 4500.00", "\"amount\": 9000.00")
            .Replace("\"subtotal\": 5700.00", "\"subtotal\": 10200.00")
            .Replace("\"taxTotal\": 1026.00", "\"taxTotal\": 1836.00")
            .Replace("\"total\": 6726.00", "\"total\": 12036.00");
        var invoice = await SettledAsync(client, gloves, "approve");
        Assert.Equal(Outcome.Hold, invoice.Decision!.Outcome);

        await client.PostAsJsonAsync($"/api/invoices/{invoice.Id}/actions",
            new { action = "confirm", expectedVersion = invoice.Version }, TestJson.Options);
        var outbox = await client.GetFromJsonAsync<JsonElement>("/api/outbox");

        Assert.Contains(outbox.EnumerateArray(), e => e.GetProperty("invoiceId").GetString() == invoice.Id
            && e.GetProperty("email").GetProperty("to").GetString() == "ar@acme-industrial.example");
    }
}
