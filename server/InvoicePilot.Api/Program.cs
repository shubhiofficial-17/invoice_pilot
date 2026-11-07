using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using InvoicePilot.Api.Accounts;
using InvoicePilot.Api.Agent;
using InvoicePilot.Api.Ai;
using InvoicePilot.Api.Audit;
using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Extraction;
using InvoicePilot.Api.Erp.Mongo;
using InvoicePilot.Api.Invoices;
using InvoicePilot.Api.Invoices.Mongo;
using InvoicePilot.Api.Matching;
using InvoicePilot.Api.Policy;
using InvoicePilot.Api.Review;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = InvoiceLimits.MaxFileBytes + 64 * 1024);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// Sign-in: an HttpOnly, SameSite cookie (same origin through nginx, so no tokens in JavaScript).
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddSingleton<UserDirectory>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "ip_auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    // An API answers 401/403; it never redirects to a login page.
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
builder.Services.AddSingleton<IAuthorizationHandler, SignedInHandler>();
builder.Services.AddAuthorization(o =>
    o.FallbackPolicy = new AuthorizationPolicyBuilder().AddRequirements(new SignedInRequirement()).Build());
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    // Keeps cookies valid across restarts (the keys live on a volume).
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath)).SetApplicationName("invoicepilot");
}

// Rate limits: uploads cost model calls; sign-in attempts are a password-guessing target.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("uploads", http => Limit(http, builder.Configuration.GetValue("RateLimits:UploadsPerMinute", 30)));
    o.AddPolicy("signin", http => Limit(http, builder.Configuration.GetValue("RateLimits:SignInsPerMinute", 10)));
});

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
    builder.Services.AddSingleton<IAuditLog, MongoAuditLog>();
}
else
{
    builder.Services.AddSingleton<IErpStore, InMemoryErpStore>();
    builder.Services.AddSingleton<IInvoiceStore, InMemoryInvoiceStore>();
    builder.Services.AddSingleton<IAuditLog, InMemoryAuditLog>();
}

// AI: any OpenAI-compatible endpoint, normally the LLM Gateway. Without one, reading is off.
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Ai"));
if (!string.IsNullOrWhiteSpace(builder.Configuration["Ai:BaseUrl"]))
{
    builder.Services.AddHttpClient<ILlmClient, OpenAiCompatibleLlmClient>();
}
else if (string.Equals(builder.Configuration["Ai:Provider"], "demo", StringComparison.OrdinalIgnoreCase))
{
    // Rule-based stand-in for demos and end-to-end tests; see DemoLlmClient.
    builder.Services.AddSingleton<ILlmClient, DemoLlmClient>();
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
builder.Services.AddHostedService<AuditInitializer>();
builder.Services.AddHostedService<BookingReconciler>();
builder.Services.AddHostedService<ExtractionWorker>();
builder.Services.AddHostedService<AgentWorker>();

var app = builder.Build();

if (app.Configuration.GetValue("ForwardedHeaders:Enabled", false))
{
    // Only when the API is reachable solely through nginx; otherwise clients could fake their IP.
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/api/health", () => Results.Ok(new HealthResponse("ok", "invoicepilot-api", storage))).AllowAnonymous();
app.MapAccountEndpoints();
app.MapGet("/api/ai/status", (ILlmClient llm) => Results.Ok(new { enabled = llm.Enabled, model = llm.Model }));
app.MapGet("/api/policy", (Microsoft.Extensions.Options.IOptions<ApPolicyOptions> p) =>
    Results.Text(ApPolicy.Describe(p.Value), "text/markdown"));
app.MapErpEndpoints();
app.MapInvoiceEndpoints();

app.Run();

static RateLimitPartition<string> Limit(HttpContext http, int perMinute)
{
    var key = http.User.Identity?.IsAuthenticated == true
        ? "user:" + http.User.Identity.Name
        : http.Connection.RemoteIpAddress?.ToString();
    if (key is null) return RateLimitPartition.GetNoLimiter("in-process"); // test server: no client address
    return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = perMinute,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });
}

public sealed record HealthResponse(string Status, string Service, string Storage);


// Lets the integration tests use WebApplicationFactory<Program>.
public partial class Program { }
