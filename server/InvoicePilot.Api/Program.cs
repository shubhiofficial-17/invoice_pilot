using System.Text.Json;
using System.Text.Json.Serialization;
using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Erp.Mongo;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
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
}
else
{
    builder.Services.AddSingleton<IErpStore, InMemoryErpStore>();
}
builder.Services.AddHostedService<ErpSeeder>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/api/health", () => Results.Ok(new HealthResponse("ok", "invoicepilot-api", storage)));
app.MapErpEndpoints();

app.Run();

public sealed record HealthResponse(string Status, string Service, string Storage);

// Lets the integration tests use WebApplicationFactory<Program>.
public partial class Program { }
