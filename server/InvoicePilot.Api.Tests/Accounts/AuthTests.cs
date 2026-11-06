using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvoicePilot.Api.Accounts;
using InvoicePilot.Api.Agent;
using InvoicePilot.Api.Erp;
using InvoicePilot.Api.Invoices;
using InvoicePilot.Api.Review;
using InvoicePilot.Api.Tests.Extraction;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static InvoicePilot.Api.Tests.AgentScript;

namespace InvoicePilot.Api.Tests.Accounts;

/// <summary>The API with sign-in on, a fake model, and (optionally) a client IP so rate limits apply.</summary>
public sealed class SignedInAppFactory : WebApplicationFactory<Program>
{
    public FakeLlmClient Llm { get; } = new();

    public bool WithClientIp { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<AuthOptions>(o => { o.Enabled = true; o.DemoPassword = "test-password"; });
            services.AddSingleton<Api.Ai.ILlmClient>(Llm);
            if (WithClientIp) services.AddSingleton<IStartupFilter, FakeClientIp>();
        });
    }

    private sealed class FakeClientIp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }
}

public static class SignIn
{
    public static async Task<HttpClient> AsAsync(this SignedInAppFactory factory, string email)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/account/signin", new { email, password = "test-password" });
        response.EnsureSuccessStatusCode();
        return client;
    }
}

public class AuthTests(SignedInAppFactory factory) : IClassFixture<SignedInAppFactory>
{
    [Fact]
    public async Task The_api_needs_a_signed_in_user_except_health()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/erp/suppliers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }

    [Theory]
    [InlineData("asha.rao@invoicepilot.example", "wrong-password")]
    [InlineData("nobody@invoicepilot.example", "test-password")]
    [InlineData("", "")]
    public async Task Bad_credentials_get_one_generic_answer(string email, string password)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/account/signin", new { email, password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("email or password is wrong", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signing_in_sets_an_http_only_cookie_and_signing_out_ends_the_session()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/account/signin", new { email = "ASHA.RAO@invoicepilot.example", password = "test-password" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ip_auth="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        var me = await client.GetFromJsonAsync<AppUser>("/api/account/me", TestJson.Options);
        Assert.Equal(("asha.rao@invoicepilot.example", Roles.Clerk), (me!.Email, me.Role));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/invoices")).StatusCode);

        await client.PostAsync("/api/account/signout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/invoices")).StatusCode);
    }

    [Fact]
    public async Task A_routed_invoice_is_approved_by_its_approver_and_actions_carry_real_names()
    {
        // Price 10% above the PO → routed to the PO's buyer, Priya.
        var reply = SampleReplies.AcmeJson
            .Replace("AC/2026/0420", "AC/2026/" + Random.Shared.Next(10_000, 99_999))
            .Replace("\"unitPrice\": 4.50, \"taxRate\": 0.18, \"amount\": 4500.00", "\"unitPrice\": 4.95, \"taxRate\": 0.18, \"amount\": 4950.00")
            .Replace("\"subtotal\": 5700.00", "\"subtotal\": 6150.00").Replace("\"taxTotal\": 1026.00", "\"taxTotal\": 1107.00")
            .Replace("\"total\": 6726.00", "\"total\": 7257.00");
        factory.Llm.Handler = Steps(reply, Standard("route_for_approval", codes: """["price_variance"]"""));
        var clerk = await factory.AsAsync("asha.rao@invoicepilot.example");
        var uploaded = await clerk.UploadAsync(TestPdfs.WithLines("Routed", Guid.NewGuid().ToString()));
        var invoice = await clerk.WaitUntilSettledAsync(uploaded.Id);
        Assert.Equal("priya.nair@invoicepilot.example", invoice.Decision!.Approver!.Email);

        var approver = await factory.AsAsync("priya.nair@invoicepilot.example");
        var tooEarly = await approver.PostAsJsonAsync($"/api/invoices/{invoice.Id}/actions", new { action = "confirm", expectedVersion = invoice.Version });
        Assert.Equal(HttpStatusCode.Forbidden, tooEarly.StatusCode); // reviewing is the clerk's step

        var routed = await (await clerk.PostAsJsonAsync($"/api/invoices/{invoice.Id}/actions",
            new { action = "confirm", expectedVersion = invoice.Version })).Content.ReadFromJsonAsync<Invoice>(TestJson.Options);
        Assert.Equal(InvoiceStatus.AwaitingApproval, routed!.Status);

        var wrongPerson = await factory.AsAsync("rahul.mehta@invoicepilot.example");
        var notTheirs = await wrongPerson.PostAsJsonAsync($"/api/invoices/{invoice.Id}/actions", new { action = "approve", expectedVersion = routed.Version });
        Assert.Equal(HttpStatusCode.Forbidden, notTheirs.StatusCode);

        var approved = await approver.PostAsJsonAsync($"/api/invoices/{invoice.Id}/actions", new { action = "approve", expectedVersion = routed.Version });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var final = await approved.Content.ReadFromJsonAsync<Invoice>(TestJson.Options);
        Assert.Equal(new[] { "asha.rao@invoicepilot.example", "priya.nair@invoicepilot.example" }, final!.Actions!.Select(a => a.Actor));

        var trail = await clerk.GetFromJsonAsync<JsonElement>($"/api/invoices/{invoice.Id}/audit");
        Assert.Equal("asha.rao@invoicepilot.example", trail.GetProperty("events")[0].GetProperty("actor").GetString());
    }
}

public class SegregationOfDutiesTests
{
    [Fact]
    public async Task Whoever_processed_an_invoice_cannot_approve_it_even_as_admin()
    {
        var store = new InMemoryInvoiceStore();
        var erp = new InMemoryErpStore();
        await erp.UpsertAsync(ErpSeed.Create());
        var review = new ReviewService(store, erp, new Api.Audit.InMemoryAuditLog(TimeProvider.System),
            Options.Create(new Api.Policy.ApPolicyOptions()), TimeProvider.System);
        var data = Matching.MatchEngineTests.Billing("PO-1001", invoiceNumber: "SOD-1");
        var read = Agent.InvoiceAgentTests.ReadInvoice(data);
        var match = Matching.MatchEngineTests.Match(data);
        var decision = await new Api.Policy.DecisionService(erp, Options.Create(new Api.Policy.ApPolicyOptions()))
            .DecideAsync(match, new AgentProposal(Outcome.RouteForApproval, "check", [], null));
        var invoice = read with { Status = InvoiceStatus.AwaitingReview, Version = 1, Match = match, Decision = decision };
        await store.AddAsync(invoice, [1]);
        var admin = new Reviewer("admin@invoicepilot.example", Roles.Admin, true);

        var routed = (await review.ActAsync(invoice.Id, new ReviewRequest("confirm", 1), admin)).Invoice!;
        var selfApproval = await review.ActAsync(invoice.Id, new ReviewRequest("approve", routed.Version), admin);

        Assert.Equal(InvoiceStatus.AwaitingApproval, routed.Status);
        Assert.Equal(403, selfApproval.Status);
        Assert.Contains("can't also approve", selfApproval.Detail);
        var manager = new Reviewer("ap.manager@invoicepilot.example", Roles.Approver, true);
        Assert.Equal(200, (await review.ActAsync(invoice.Id, new ReviewRequest("approve", routed.Version), manager)).Status);
    }
}

public class RateLimitTests
{
    [Fact]
    public async Task Sign_in_attempts_are_limited_per_client_address()
    {
        await using var factory = new SignedInAppFactory { WithClientIp = true };
        var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/account/signin", new { email = "x@y.example", password = "guess" + i })).StatusCode);
        }

        Assert.Equal(10, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }
}

public class PasswordTests
{
    [Fact]
    public void Hashes_are_salted_and_verify()
    {
        var a = Passwords.Hash("secret");
        var b = Passwords.Hash("secret");

        Assert.NotEqual(a, b);
        Assert.StartsWith("v1.210000.", a);
        Assert.True(Passwords.Verify("secret", a));
        Assert.False(Passwords.Verify("Secret", a));
        Assert.False(Passwords.Verify("secret", "garbage"));
    }
}
