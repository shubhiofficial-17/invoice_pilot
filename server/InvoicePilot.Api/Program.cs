using System.Text.Json;
using System.Text.Json.Serialization;
using InvoicePilot.Api.Agent;
using InvoicePilot.Api.Ai;
using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Extraction;
using InvoicePilot.Api.Erp.Mongo;
using InvoicePilot.Api.Invoices;
using InvoicePilot.Api.Invoices.Mongo;
using InvoicePilot.Api.Matching;
using InvoicePilot.Api.Policy;
using InvoicePilot.Api.Review;
using Microsoft.AspNetCore.Http.Features;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = InvoiceLimits.MaxFileBytes + 64 * 1024);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// Storage: MongoDB when a connection string is configured, otherwise in memory.
var mongoConnection = builder.Configuration.GetConnectionString("Mongo");
var storage = string.IsNullOrWhiteSpace(mongoConnection) ? "memory" : "mongo";
if (storage == "mongo")
{
    MongoSetup.Configure();
    var url = MongoUrl.Create(mongoConnection);
    builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(url));
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(url.DatabaseName ?? "invoicepilot"));
    builder.Services.AddSingleton<IErpStore, MongoErpStore>();
    builder.Services.AddSingleton<IInvoiceStore, MongoInvoiceStore>();
}
else
{
    builder.Services.AddSingleton<IErpStore, InMemoryErpStore>();
    builder.Services.AddSingleton<IInvoiceStore, InMemoryInvoiceStore>();
}

// AI: any OpenAI-compatible endpoint, normally the LLM Gateway. Without one, reading is off.
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Ai"));
if (!string.IsNullOrWhiteSpace(builder.Configuration["Ai:BaseUrl"]))
{
    builder.Services.AddHttpClient<ILlmClient, OpenAiCompatibleLlmClient>();
}
else
{
    builder.Services.AddSingleton<ILlmClient, DisabledLlmClient>();
}
builder.Services.AddTransient<InvoiceExtractor>();
builder.Services.AddSingleton<ExtractionQueue>();
builder.Services.AddTransient<MatchService>();
builder.Services.Configure<ApPolicyOptions>(builder.Configuration.GetSection("Policy"));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddTransient<InvoiceAgent>();
builder.Services.AddTransient<DecisionService>();
builder.Services.AddTransient<ReviewService>();
builder.Services.AddSingleton<AgentQueue>();

builder.Services.AddHostedService<ErpSeeder>();
builder.Services.AddHostedService<InvoiceStoreInitializer>();
builder.Services.AddHostedService<BookingReconciler>();
builder.Services.AddHostedService<ExtractionWorker>();
builder.Services.AddHostedService<AgentWorker>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/api/health", () => Results.Ok(new HealthResponse("ok", "invoicepilot-api", storage)));
app.MapGet("/api/ai/status", (ILlmClient llm) => Results.Ok(new { enabled = llm.Enabled, model = llm.Model }));
app.MapGet("/api/policy", (Microsoft.Extensions.Options.IOptions<ApPolicyOptions> p) =>
    Results.Text(ApPolicy.Describe(p.Value), "text/markdown"));
app.MapErpEndpoints();
app.MapInvoiceEndpoints();

app.Run();

public sealed record HealthResponse(string Status, string Service, string Storage);

// Lets the integration tests use WebApplicationFactory<Program>.
public partial class Program { }
