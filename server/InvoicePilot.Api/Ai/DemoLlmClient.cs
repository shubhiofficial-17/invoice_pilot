using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InvoicePilot.Api.Agent;
using InvoicePilot.Api.Matching;
using InvoicePilot.Api.Policy;
using Microsoft.Extensions.Options;

namespace InvoicePilot.Api.Ai;

/// <summary>
/// NOT an AI. A rule-based stand-in so the app can be demonstrated (and tested end to end) without an
/// API key: it reads PDFs in the layout produced by <c>InvoicePdf</c>, and as the "agent" it calls the
/// real tools in the usual order and submits whatever the policy requires. It exercises every part of
/// the pipeline except the model's judgement. Turn it on with Ai:Provider=demo.
/// </summary>
public sealed partial class DemoLlmClient(IOptions<ApPolicyOptions> policy) : ILlmClient
{
    public bool Enabled => true;

    public string Model => "demo-rules (not an AI)";

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
        Task.FromResult(request.Tools is null ? Extract(request) : Investigate(request));

    private LlmResponse Extract(LlmRequest request)
    {
        var text = request.Messages.LastOrDefault(m => m.Role == "user")?.Text ?? "";
        var start = text.IndexOf("<<<INVOICE", StringComparison.Ordinal);
        var end = text.IndexOf("INVOICE>>>", StringComparison.Ordinal);
        var body = start >= 0 && end > start ? text[(start + 10)..end] : "";
        var lines = body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        string? Find(Regex r) => lines.Select(l => r.Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim();

        var items = new JsonArray();
        foreach (var row in lines.Where(l => l.Contains(" | ") && !l.StartsWith("Description |", StringComparison.Ordinal)))
        {
            var p = row.Split(" | ");
            if (p.Length != 6) continue;
            items.Add(new JsonObject
            {
                ["description"] = p[0],
                ["sku"] = p[1] == "-" ? null : p[1],
                ["quantity"] = Num(p[2]),
                ["unitPrice"] = Num(p[3]),
                ["taxRate"] = Num(p[4].TrimEnd('%')) is { } pct ? pct / 100m : null,
                ["amount"] = Num(p[5]),
            });
        }

        var dates = lines.Select(l => DatesLine().Match(l)).FirstOrDefault(m => m.Success);
        var totalLine = lines.Select(l => TotalLine().Match(l)).FirstOrDefault(m => m.Success);
        var tax = Find(TaxIdLine());
        var json = new JsonObject
        {
            ["supplierName"] = lines.FirstOrDefault(),
            ["supplierTaxId"] = tax is null or "-" ? null : tax,
            ["invoiceNumber"] = Find(NumberLine()),
            ["invoiceDate"] = dates?.Groups[1].Value,
            ["dueDate"] = dates?.Groups[2].Value,
            ["poNumber"] = Find(PoLine()),
            ["currency"] = totalLine?.Groups[1].Value,
            ["lines"] = items,
            ["subtotal"] = Num(Find(SubtotalLine())),
            ["taxTotal"] = Num(Find(TaxLine())),
            ["total"] = totalLine is null ? null : Num(totalLine.Groups[2].Value),
        };
        return new LlmResponse(json.ToJsonString(), Model, new LlmUsage(0, 0));
    }

    private LlmResponse Investigate(LlmRequest request)
    {
        var results = request.Messages.Where(m => m.Role == "tool").ToList();
        LlmResponse Call(string tool, string args = "{}") =>
            new("", Model, new LlmUsage(0, 0), [new LlmToolCall($"demo_{results.Count}", tool, args)]);

        switch (results.Count)
        {
            case 0: return Call("get_invoice");
            case 1: return Call("run_three_way_match");
            case 2: return Call("read_ap_policy");
            case 3:
                var match = JsonSerializer.Deserialize<MatchReport>(results[1].Text, AgentJson.Options)!;
                var verdict = PolicyEngine.Evaluate(match, policy.Value, null);
                var codes = verdict.Rules.Where(r => r.Outcome == verdict.Required && r.FindingCode != "high_value")
                    .Select(r => r.FindingCode).Distinct().ToList();
                var reason = codes.Count == 0
                    ? $"Demo rules: the policy allows {verdict.Required}."
                    : $"Demo rules: {string.Join(", ", codes)} → {verdict.Required}.";
                var args = new JsonObject
                {
                    ["outcome"] = verdict.Required switch
                    {
                        Outcome.Approve => "approve",
                        Outcome.RouteForApproval => "route_for_approval",
                        Outcome.Hold => "hold",
                        _ => "reject",
                    },
                    ["reason"] = reason,
                    ["finding_codes"] = new JsonArray(codes.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
                };
                if (verdict.Required == Outcome.Hold)
                {
                    var problems = string.Join("\n", match.Findings.Where(f => codes.Contains(f.Code)).Select(f => "- " + f.Message));
                    args["supplier_email"] = new JsonObject
                    {
                        ["subject"] = $"Invoice on hold: PO {match.PoNumber ?? "missing"}",
                        ["body"] = $"Hello,\n\nWe can't process your invoice yet:\n{problems}\n\nPlease send a corrected invoice.\n\nAccounts Payable",
                    };
                }
                return Call(SubmitDecisionTool.ToolName, args.ToJsonString());
            default:
                return new LlmResponse("Done.", Model, new LlmUsage(0, 0));
        }
    }

    private static decimal? Num(string? s) =>
        decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    [GeneratedRegex(@"^Invoice No:\s*(.+)$")] private static partial Regex NumberLine();
    [GeneratedRegex(@"^Tax ID:\s*(.+)$")] private static partial Regex TaxIdLine();
    [GeneratedRegex(@"^Purchase Order:\s*(.+)$")] private static partial Regex PoLine();
    [GeneratedRegex(@"Invoice Date:\s*(\d{4}-\d{2}-\d{2})\s+Due Date:\s*(\d{4}-\d{2}-\d{2})")] private static partial Regex DatesLine();
    [GeneratedRegex(@"^Subtotal:\s*(.+)$")] private static partial Regex SubtotalLine();
    [GeneratedRegex(@"^Tax:\s*(.+)$")] private static partial Regex TaxLine();
    [GeneratedRegex(@"^Total:\s*([A-Z]{3})\s+(.+)$")] private static partial Regex TotalLine();
}
