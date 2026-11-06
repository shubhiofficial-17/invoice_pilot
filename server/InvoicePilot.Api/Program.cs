var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/api/health", () => Results.Ok(new HealthResponse("ok", "invoicepilot-api")));

app.Run();

public sealed record HealthResponse(string Status, string Service);

// Lets the integration tests use WebApplicationFactory<Program>.
public partial class Program { }
