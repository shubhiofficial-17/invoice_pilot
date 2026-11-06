using System.Globalization;

namespace InvoicePilot.Api.Policy;

/// <summary>
/// The AP policy's numbers. The text the agent reads (<see cref="ApPolicy.Describe"/>) and the
/// rules the code enforces (<see cref="PolicyEngine"/>) are both built from this one object,
/// so they can't disagree.
/// </summary>
public sealed class ApPolicyOptions
{
    /// <summary>Price above the PO by at most this many percent is accepted.</summary>
    public decimal PriceVarianceTolerancePct { get; set; } = 2.0m;

    /// <summary>Above this gross total (per currency), approval needs the finance controller.</summary>
    public Dictionary<string, decimal> ControllerApprovalAbove { get; set; } = new()
    {
        ["INR"] = 500_000m,
        ["USD"] = 10_000m,
    };

    public string ApManagerEmail { get; set; } = "ap.manager@invoicepilot.example";

    public string ControllerEmail { get; set; } = "finance.controller@invoicepilot.example";
}

public static class ApPolicy
{
    public static string Describe(ApPolicyOptions o)
    {
        var limits = string.Join(", ", o.ControllerApprovalAbove
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key} {kv.Value.ToString("N0", CultureInfo.InvariantCulture)}"));
        var tol = o.PriceVarianceTolerancePct.ToString("0.##", CultureInfo.InvariantCulture);

        return $"""
            # Accounts payable policy

            Decide every invoice with exactly one outcome. When several rules apply, the strictest wins:
            reject > hold > route_for_approval > approve.

            ## reject
            Do not pay, and do not ask the supplier to fix it:
            - the invoice was already booked (duplicate_invoice),
            - the supplier is unknown (supplier_not_found) or blocked (supplier_blocked),
            - the PO is cancelled (po_cancelled).

            ## hold (and email the supplier)
            The supplier must send a corrected invoice or information. Write the email: say exactly what is wrong,
            with the numbers, and what you need back. Cases:
            - billed quantity above what was received or ordered,
            - wrong tax rate or tax amount, wrong currency,
            - a line that is not on the PO, invoice arithmetic that does not add up,
            - no PO number, invoice number or total on the invoice,
            - the invoice would take the PO above its value (po_amount_exceeded).

            ## route_for_approval
            Someone inside the company must decide. The approver is chosen by the system, not by you:
            - price above the PO by more than {tol}% → the PO's buyer,
            - PO closed or not found, supplier not on the PO, supplier unverified, possible duplicate → the AP manager,
            - gross total above {limits} → the finance controller (even if everything else is clean).

            ## approve
            Only when none of the above applies. Allowed: prices above the PO by at most {tol}%, prices below the PO,
            payment terms shorter than agreed (pay on the agreed terms), a missing invoice date.
            Approval is a proposal: a person confirms it before anything is paid.
            """;
    }
}
