using InvoicePilot.Api.Agent;
using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Invoices;
using InvoicePilot.Api.Policy;
using Microsoft.Extensions.Options;

namespace InvoicePilot.Api.Review;

/// <param name="Action">confirm | override (reviewer); approve | reject (approver).</param>
/// <param name="ExpectedVersion">The version the person was looking at. A stale view gets 409.</param>
public sealed record ReviewRequest(string Action, int ExpectedVersion, Outcome? Outcome = null, string? Note = null);

public sealed record ReviewResult(Invoice? Invoice, int Status, string? Title = null, string? Detail = null)
{
    public static ReviewResult Ok(Invoice i) => new(i, 200);
    public static ReviewResult Fail(int status, string title, string detail) => new(null, status, title, detail);
}

/// <summary>
/// The human checkpoint. Nothing that moves money happens without a person's action here, and a
/// person can't go below what the policy requires: a clerk can't approve what the policy routes or
/// holds, and nobody can approve what the policy rejects.
/// </summary>
public sealed class ReviewService(IInvoiceStore store, IErpStore erp, IOptions<ApPolicyOptions> policy, TimeProvider clock)
{
    public async Task<ReviewResult> ActAsync(string id, ReviewRequest request, string actor, CancellationToken ct = default)
    {
        var invoice = await store.GetAsync(id, ct);
        if (invoice is null) return ReviewResult.Fail(404, "Not found", $"Invoice {id} does not exist.");
        if (invoice.Version != request.ExpectedVersion)
        {
            return ReviewResult.Fail(409, "Changed meanwhile", "The invoice changed since you loaded it. Reload and review again.");
        }
        var note = request.Note?.Trim();
        if (note is { Length: > 2000 }) return ReviewResult.Fail(422, "Note too long", "Keep the note under 2000 characters.");

        return invoice.Status switch
        {
            InvoiceStatus.AwaitingReview or InvoiceStatus.InvestigationFailed => await ReviewAsync(invoice, request, note, actor, ct),
            InvoiceStatus.AwaitingApproval => await ApproveStepAsync(invoice, request, note, actor, ct),
            _ => ReviewResult.Fail(409, "Nothing to decide", $"The invoice is {invoice.Status}; there is nothing to review."),
        };
    }

    private async Task<ReviewResult> ReviewAsync(Invoice invoice, ReviewRequest request, string? note, string actor, CancellationToken ct)
    {
        var decision = invoice.Decision;
        if (decision is null) return ReviewResult.Fail(409, "No recommendation", "Investigate the invoice first.");

        Outcome outcome;
        switch (request.Action)
        {
            case "confirm":
                outcome = decision.Outcome;
                break;
            case "override":
                if (request.Outcome is not { } chosen) return ReviewResult.Fail(422, "Outcome required", "Choose the outcome you want instead.");
                if (string.IsNullOrWhiteSpace(note) || note.Length < 5)
                {
                    return ReviewResult.Fail(422, "Note required", "Explain in a note why you are changing the recommendation.");
                }
                if (PolicyEngine.Rank(chosen) < PolicyEngine.Rank(decision.Policy.Required))
                {
                    return ReviewResult.Fail(422, "Not allowed by policy",
                        $"The policy requires at least \"{decision.Policy.Required}\". A reviewer can make it stricter, not looser.");
                }
                outcome = chosen;
                break;
            default:
                return ReviewResult.Fail(422, "Unknown action", "Use confirm or override.");
        }

        var action = new HumanAction(actor, request.Action, outcome, note, Now());
        return await ApplyAsync(invoice, outcome, action, ct);
    }

    private async Task<ReviewResult> ApproveStepAsync(Invoice invoice, ReviewRequest request, string? note, string actor, CancellationToken ct)
    {
        var approver = invoice.Decision?.Approver;
        switch (request.Action)
        {
            case "approve":
                // An approver can accept a routed invoice, but never one the policy rejects.
                if (invoice.Decision?.Policy.Required == Outcome.Reject)
                {
                    return ReviewResult.Fail(422, "Not allowed by policy", "The policy rejects this invoice; it can't be approved.");
                }
                return await ApplyAsync(invoice, Outcome.Approve,
                    new HumanAction(actor, "approve", Outcome.Approve, note, Now()), ct);
            case "reject":
                if (string.IsNullOrWhiteSpace(note) || note.Length < 5)
                {
                    return ReviewResult.Fail(422, "Note required", "Say why you are rejecting it.");
                }
                return await ApplyAsync(invoice, Outcome.Reject, new HumanAction(actor, "reject", Outcome.Reject, note, Now()), ct);
            default:
                return ReviewResult.Fail(422, "Unknown action",
                    $"This invoice waits for {approver?.Role ?? "an approver"}: use approve or reject.");
        }
    }

    private async Task<ReviewResult> ApplyAsync(Invoice invoice, Outcome outcome, HumanAction action, CancellationToken ct)
    {
        var actions = (invoice.Actions ?? Array.Empty<HumanAction>()).Append(action).ToList();
        var updated = invoice with { Actions = actions, UpdatedAt = Now(), FailureReason = null };

        switch (outcome)
        {
            case Outcome.Approve:
                var posted = ToPosting(invoice);
                if (posted is null)
                {
                    return ReviewResult.Fail(422, "Can't book", "Supplier, invoice number, date, currency and total are all needed to book an invoice.");
                }
                // Claim first (the version check makes sure only one person wins), then book.
                var claimed = await store.TryUpdateAsync(updated with { Status = InvoiceStatus.Approved, Booking = Key(posted) }, ct);
                if (claimed is null) return Conflict();
                var booked = await BookAsync(claimed, posted, ct);
                return booked ?? ReviewResult.Ok(claimed);

            case Outcome.RouteForApproval:
                return Saved(await store.TryUpdateAsync(updated with { Status = InvoiceStatus.AwaitingApproval }, ct));

            case Outcome.Hold:
                var email = invoice.Decision?.SupplierEmail;
                if (email is null && invoice.Match?.SupplierId is { } supplierId && await erp.GetSupplierAsync(supplierId, ct) is { } supplier
                    && invoice.Decision is not null)
                {
                    email = DecisionService.TemplateEmail(supplier, invoice.Match, invoice.Decision.Policy);
                }
                if (email is null) return ReviewResult.Fail(422, "No supplier to email", "The supplier is unknown, so a hold can't be sent. Reject or route it.");
                var outbox = new OutboxEmail(email.To, email.Subject, email.Body, Now());
                return Saved(await store.TryUpdateAsync(updated with { Status = InvoiceStatus.OnHold, Outbox = outbox }, ct));

            default:
                return Saved(await store.TryUpdateAsync(updated with { Status = InvoiceStatus.Rejected }, ct));
        }
    }

    /// <summary>
    /// Books an approved invoice in the ERP. Idempotent: if the booking already exists and is ours,
    /// that's success; if it belongs to another invoice, this one is sent back for review.
    /// </summary>
    public async Task<ReviewResult?> BookAsync(Invoice approved, PostedInvoice posted, CancellationToken ct)
    {
        if (await erp.AddPostedInvoiceAsync(posted, ct)) return null;

        var existing = (await erp.ListPostedInvoicesAsync(posted.SupplierId, ct))
            .FirstOrDefault(p => ErpNormalizer.InvoiceKey(p) == ErpNormalizer.InvoiceKey(posted));
        if (existing?.SourceRef == approved.Id) return null; // booked on an earlier attempt

        await store.TryUpdateAsync(approved with
        {
            Status = InvoiceStatus.AwaitingReview,
            Booking = null,
            FailureReason = $"Invoice {posted.InvoiceNumber} from {posted.SupplierId} was booked by someone else in the meantime.",
            UpdatedAt = Now(),
        }, ct);
        return ReviewResult.Fail(409, "Already booked", $"Invoice {posted.InvoiceNumber} from {posted.SupplierId} is already in the ERP.");
    }

    public static PostedInvoice? ToPosting(Invoice invoice)
    {
        var data = invoice.Extraction?.Data;
        var supplierId = invoice.Match?.SupplierId;
        if (data?.InvoiceNumber is null || data.InvoiceDate is null || data.Currency is null || data.Total is null || supplierId is null)
        {
            return null;
        }
        return new PostedInvoice(supplierId, data.InvoiceNumber, data.InvoiceDate.Value, invoice.Match?.PoNumber,
            data.Currency, data.Total.Value, PostedInvoiceStatus.Posted, invoice.Id);
    }

    private static string Key(PostedInvoice p) => ErpNormalizer.InvoiceKey(p);

    private static ReviewResult Saved(Invoice? saved) => saved is null ? Conflict() : ReviewResult.Ok(saved);

    private static ReviewResult Conflict() =>
        ReviewResult.Fail(409, "Changed meanwhile", "Someone else acted on this invoice first. Reload to see what happened.");

    private DateTimeOffset Now()
    {
        var utc = clock.GetUtcNow();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
    }
}

/// <summary>
/// On startup, finishes bookings interrupted between "approved" and "booked in the ERP"
/// (a crash after the claim). Booking is idempotent, so running this twice is harmless.
/// </summary>
public sealed class BookingReconciler(IInvoiceStore store, IServiceScopeFactory scopes, ILogger<BookingReconciler> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var review = scope.ServiceProvider.GetRequiredService<ReviewService>();
        foreach (var invoice in await store.ListByStatusAsync(InvoiceStatus.Approved, ct))
        {
            if (ReviewService.ToPosting(invoice) is { } posted && await review.BookAsync(invoice, posted, ct) is { } failed)
            {
                log.LogWarning("Could not complete booking of {InvoiceId}: {Detail}", invoice.Id, failed.Detail);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
