using System.Text.Json;
using System.Text.Json.Serialization;
using InvoicePilot.Api.Ai;
using InvoicePilot.Api.Evaluation;
using Microsoft.Extensions.Options;

// InvoicePilot evaluation.
//   dotnet run --project eval/InvoicePilot.Eval -- --mode engine
//   dotnet run --project eval/InvoicePilot.Eval -- --mode agent --base-url http://localhost:5000/v1/ --model gpt-4o-mini \
//       --api-key $KEY --price-in 0.15 --price-out 0.60
// Options: --count 100 --seed 42 --out eval/results --pdfs (also write the generated PDFs)
// The base URL and key can also come from AI__BASEURL / AI__APIKEY / AI__MODEL.

var options = Args.Parse(args);
var mode = options.Get("mode", "engine");
var cases = EvalScenarios.Generate(int.Parse(options.Get("count", "100")), int.Parse(options.Get("seed", "42")));
var outDir = options.Get("out", Path.Combine("eval", "results"));
Directory.CreateDirectory(outDir);

if (options.Has("pdfs"))
{
    var pdfDir = Path.Combine(outDir, "pdfs");
    Directory.CreateDirectory(pdfDir);
    foreach (var c in cases) File.WriteAllBytes(Path.Combine(pdfDir, $"{c.Id}-{c.Category}.pdf"), c.RenderPdf());
    Console.WriteLine($"Wrote {cases.Count} PDFs to {pdfDir}");
}

EvalReport report;
if (mode == "engine")
{
    report = await EvalRunner.RunEngineAsync(cases);
}
else if (mode == "agent")
{
    var ai = new AiOptions
    {
        BaseUrl = options.Get("base-url", Environment.GetEnvironmentVariable("AI__BASEURL") ?? ""),
        ApiKey = options.Get("api-key", Environment.GetEnvironmentVariable("AI__APIKEY") ?? ""),
        Model = options.Get("model", Environment.GetEnvironmentVariable("AI__MODEL") ?? "gpt-4o-mini"),
        TimeoutSeconds = 120,
    };
    if (string.IsNullOrWhiteSpace(ai.BaseUrl))
    {
        Console.Error.WriteLine("Agent mode needs --base-url (or AI__BASEURL), e.g. your LLM Gateway's /v1/ endpoint.");
        return 2;
    }
    var llm = new OpenAiCompatibleLlmClient(new HttpClient(), Options.Create(ai));
    var prices = options.Has("price-in")
        ? new TokenPrices(decimal.Parse(options.Get("price-in", "0")), decimal.Parse(options.Get("price-out", "0")))
        : null;
    report = await EvalRunner.RunAgentAsync(cases, llm, prices, progress: r =>
        Console.WriteLine($"{r.Id} {r.Category,-24} expected {r.Expected,-16} got {r.Final?.ToString() ?? "failed",-16} {(r.Correct ? "ok" : "MISS")}"));
}
else
{
    Console.Error.WriteLine($"Unknown mode \"{mode}\". Use engine or agent.");
    return 2;
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
var stem = Path.Combine(outDir, $"{report.Mode}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
File.WriteAllText(stem + ".json", JsonSerializer.Serialize(report, json));
File.WriteAllText(stem + ".md", EvalRunner.ToMarkdown(report));
File.WriteAllText(Path.Combine(outDir, $"{report.Mode}-latest.md"), EvalRunner.ToMarkdown(report));

Console.WriteLine();
Console.WriteLine($"Accuracy {report.Accuracy}% ({report.Correct}/{report.Count}), false approvals {report.FalseApprovals}, failures {report.Failures}");
if (report.AgentProposalAccuracy is { } pa) Console.WriteLine($"Agent proposals {pa}% correct before the guardrail; {report.Overrides} overridden");
if (report.EstimatedCostUsd is { } cost) Console.WriteLine($"Estimated cost ${cost} (${Math.Round(cost / Math.Max(1, report.Count), 5)} per invoice)");
Console.WriteLine($"Report: {stem}.md");

// Engine mode is a correctness check of the rules: fail the process (and CI) on any miss.
return mode == "engine" && (report.Correct != report.Count || report.FalseApprovals > 0) ? 1 : 0;

internal sealed class Args
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(string[] args)
    {
        var a = new Args();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
            a._values[key] = value;
        }
        return a;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string Get(string key, string fallback) => _values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
}
