using System.Text.Json;
using System.Text.Json.Serialization;
using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Erp.Mongo;
using InvoicePilot.Api.Invoices;
using InvoicePilot.Api.Invoices.Mongo;
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
builder.Services.AddHostedService<ErpSeeder>();
builder.Services.AddHostedService<InvoiceStoreInitializer>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/api/health", () => Results.Ok(new HealthResponse("ok", "invoicepilot-api", storage)));
app.MapErpEndpoints();
app.MapInvoiceEndpoints();

app.Run();

public sealed record HealthResponse(string Status, string Service, string Storage);

// Lets the integration tests use WebApplicationFactory<Program>.
public partial class Program { }
