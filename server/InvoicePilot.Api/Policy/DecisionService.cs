using System.Text;
using InvoicePilot.Api.Agent;
using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Matching;
using Microsoft.Extensions.Options;

namespace InvoicePilot.Api.Policy;

/// <summary>Agent: its proposal stood. PolicyOverride: the policy required something stricter. PolicyOnly: no proposal.</summary>
public enum DecisionSource { Agent, PolicyOverride, PolicyOnly }

/// <summary>The recommendation a person reviews. Final until a person confirms or changes it.</summary>
public sealed record Decision(
    Outcome Outcome,
    DecisionSource Source,
    string Reason,
    Approver? Approver,
    SupplierEmail? SupplierEmail,
    PolicyVerdict Policy,
    Outcome? AgentOutcome);

/// <summary>
/// The guardrail between the agent and any action: the final outcome is the stricter of what
/// the agent proposed and what the policy requires. The agent may be more careful than the
/// policy, never less.
/// </summary>
public sealed class DecisionService(IErpStore erp, IOptions<ApPolicyOptions> options)
{
    public async Task<Decision> DecideAsync(MatchReport match, AgentProposal? proposal, CancellationToken ct = default)
    {
        var po = match.PoNumber is null ? null : await erp.GetPurchaseOrderAsync(match.PoNumber, ct);
        var verdict = PolicyEngine.Evaluate(match, options.Value, po?.BuyerEmail);

        if (proposal is null)
        {
            return await BuildAsync(verdict.Required, DecisionSource.PolicyOnly,
                "The agent did not finish, so this is the policy's outcome from the 3-way match alone. " + Summary(verdict),
                verdict, match, null, null, ct);
        }

        if (PolicyEngine.Rank(proposal.Outcome) >= PolicyEngine.Rank(verdict.Required))
        {
            return await BuildAsync(proposal.Outcome, DecisionSource.Agent, proposal.Reason, verdict, match,
                proposal.SupplierEmail, proposal.Outcome, ct);
        }

        return await BuildAsync(verdict.Required, DecisionSource.PolicyOverride,
            $"The agent proposed {Words(proposal.Outcome)}, but the policy requires {Words(verdict.Required)}. {Summary(verdict)} Agent's reason: {proposal.Reason}",
            verdict, match, null, proposal.Outcome, ct);
    }

    private async Task<Decision> BuildAsync(Outcome outcome, DecisionSource source, string reason, PolicyVerdict verdict,
        MatchReport match, SupplierEmail? agentEmail, Outcome? agentOutcome, CancellationToken ct)
    {
        Approver? approver = outcome == Outcome.RouteForApproval
            ? verdict.Approver ?? new Approver("ap_manager", options.Value.ApManagerEmail)
            : null;

        SupplierEmail? email = null;
        if (outcome == Outcome.Hold)
        {
            email = agentEmail;
            if (email is null && match.SupplierId is not null && await erp.GetSupplierAsync(match.SupplierId, ct) is { } supplier)
            {
                email = TemplateEmail(supplier, match, verdict);
            }
        }

        return new Decision(outcome, source, reason, approver, email, verdict, agentOutcome);
    }

    /// <summary>A plain, factual email listing what must be corrected, used when the agent didn't write one.</summary>
    public static SupplierEmail TemplateEmail(Supplier supplier, MatchReport match, PolicyVerdict verdict)
    {
        var holdCodes = verdict.Rules.Where(r => r.Outcome == Outcome.Hold).Select(r => r.FindingCode).ToHashSet();
        var body = new StringBuilder();
        body.AppendLine($"Dear {supplier.Name},");
        body.AppendLine();
        body.AppendLine($"We can't process your invoice{(match.PoNumber is null ? "" : $" for PO {match.PoNumber}")} yet:");
        foreach (var f in match.Findings.Where(f => holdCodes.Contains(f.Code)))
        {
            body.AppendLine($"- {f.Message}");
        }
        body.AppendLine();
        body.AppendLine("Please send a corrected invoice (or a credit note) that references the PO.");
        body.AppendLine();
        body.AppendLine("Accounts Payable");
        return new SupplierEmail(supplier.RemitEmail, $"Invoice on hold{(match.PoNumber is null ? "" : $": PO {match.PoNumber}")}", body.ToString().Trim());
    }

    private static string Summary(PolicyVerdict v)
    {
        var top = v.Rules.Where(r => r.Outcome == v.Required).Select(r => r.FindingCode).Distinct().ToList();
        return top.Count == 0 ? "No findings need action." : $"Because of: {string.Join(", ", top)}.";
    }

    private static string Words(Outcome o) => o switch
    {
        Outcome.Approve => "approve",
        Outcome.RouteForApproval => "route for approval",
        Outcome.Hold => "hold",
        _ => "reject",
    };
}
