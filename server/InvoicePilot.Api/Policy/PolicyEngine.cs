using InvoicePilot.Api.Agent;
using InvoicePilot.Api.Matching;

namespace InvoicePilot.Api.Policy;

public sealed record Approver(string Role, string Email);

/// <summary>Why the policy requires an outcome: one entry per finding that matters.</summary>
public sealed record PolicyRule(string FindingCode, Outcome Outcome, string Why);

public sealed record PolicyVerdict(Outcome Required, IReadOnlyList<PolicyRule> Rules, Approver? Approver);

/// <summary>
/// The AP policy as code. It looks only at the deterministic match report, so the outcome it
/// requires can't be talked out of by a model or by text on an invoice.
/// </summary>
public static class PolicyEngine
{
    private static readonly HashSet<string> RejectCodes =
        ["duplicate_invoice", "supplier_not_found", "supplier_blocked", "po_cancelled"];

    private static readonly HashSet<string> HoldCodes =
    [
        "quantity_exceeds_received", "quantity_exceeds_ordered", "tax_rate_mismatch", "tax_amount_mismatch",
        "currency_mismatch", "unmatched_line", "line_amount_mismatch", "subtotal_mismatch", "total_mismatch",
        "invalid_line", "missing_po_number", "missing_invoice_number", "missing_total", "no_lines",
        "unknown_currency", "po_amount_exceeded",
    ];

    private static readonly HashSet<string> ApManagerCodes =
        ["po_closed", "po_not_found", "supplier_not_on_po", "supplier_unverified", "possible_duplicate"];

    private static readonly HashSet<string> AllowedCodes =
        ["price_below_po", "payment_terms_shorter", "missing_invoice_date"];

    public static int Rank(Outcome o) => o switch
    {
        Outcome.Approve => 0,
        Outcome.RouteForApproval => 1,
        Outcome.Hold => 2,
        Outcome.Reject => 3,
        _ => 3,
    };

    public static PolicyVerdict Evaluate(MatchReport match, ApPolicyOptions options, string? poBuyerEmail)
    {
        var rules = new List<PolicyRule>();
        var needsBuyer = false;

        foreach (var f in match.Findings)
        {
            if (RejectCodes.Contains(f.Code))
            {
                rules.Add(new(f.Code, Outcome.Reject, "Must not be paid."));
            }
            else if (HoldCodes.Contains(f.Code))
            {
                rules.Add(new(f.Code, Outcome.Hold, "The supplier must correct the invoice."));
            }
            else if (ApManagerCodes.Contains(f.Code))
            {
                rules.Add(new(f.Code, Outcome.RouteForApproval, "The AP manager must decide."));
            }
            else if (f.Code == "price_variance")
            {
                if (f.VariancePct is { } pct && pct <= options.PriceVarianceTolerancePct)
                {
                    rules.Add(new(f.Code, Outcome.Approve, $"Within the {options.PriceVarianceTolerancePct}% price tolerance."));
                }
                else
                {
                    rules.Add(new(f.Code, Outcome.RouteForApproval, $"Above the {options.PriceVarianceTolerancePct}% price tolerance: the buyer must approve."));
                    needsBuyer = true;
                }
            }
            else if (AllowedCodes.Contains(f.Code))
            {
                rules.Add(new(f.Code, Outcome.Approve, "Allowed by policy."));
            }
            else
            {
                // A finding the policy doesn't know: fail safe, never approve it silently.
                var outcome = f.Severity switch
                {
                    Severity.Block => Outcome.Reject,
                    Severity.Exception or Severity.Warning => Outcome.RouteForApproval,
                    _ => Outcome.Approve,
                };
                rules.Add(new(f.Code, outcome, "Not covered by the policy; handled cautiously."));
            }
        }

        var highValue = match.Currency is { } currency && match.InvoiceTotal is { } total
            && options.ControllerApprovalAbove.TryGetValue(currency, out var limit) && total > limit;
        if (highValue)
        {
            rules.Add(new("high_value", Outcome.RouteForApproval,
                $"Total {match.InvoiceTotal} {match.Currency} is above the controller limit {options.ControllerApprovalAbove[match.Currency!]}."));
        }

        var required = rules.Count == 0 ? Outcome.Approve : rules.MaxBy(r => Rank(r.Outcome))!.Outcome;

        Approver? approver = null;
        if (required == Outcome.RouteForApproval)
        {
            approver = highValue ? new Approver("finance_controller", options.ControllerEmail)
                : needsBuyer && poBuyerEmail is not null ? new Approver("buyer", poBuyerEmail)
                : new Approver("ap_manager", options.ApManagerEmail);
        }

        return new PolicyVerdict(required, rules, approver);
    }
}
