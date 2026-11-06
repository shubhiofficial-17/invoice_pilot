using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace InvoicePilot.Api.Accounts;

public sealed class AuthOptions
{
    /// <summary>Turn sign-in off only for local experiments and tests.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Password of the demo users. Set your own (Auth__DemoPassword) anywhere but your laptop.</summary>
    public string DemoPassword { get; set; } = "invoicepilot-demo";
}

public static class Roles
{
    public const string Clerk = "clerk";
    public const string Approver = "approver";
    public const string Admin = "admin";
}

public sealed record AppUser(string Email, string Name, string Role);

/// <summary>PBKDF2-SHA256, 210,000 iterations, random 16-byte salt. Stored as v1.iterations.salt.hash.</summary>
public static class Passwords
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>
/// The demo's people: one AP clerk and the approvers the policy routes to. A real deployment would
/// sign in through the company's identity provider and map its groups to these roles.
/// </summary>
public sealed class UserDirectory
{
    private readonly Dictionary<string, (AppUser User, string Hash)> _users;
    private readonly string _dummyHash;

    public UserDirectory(IOptions<AuthOptions> options)
    {
        var password = options.Value.DemoPassword;
        AppUser[] users =
        [
            new("asha.rao@invoicepilot.example", "Asha Rao (AP clerk)", Roles.Clerk),
            new("priya.nair@invoicepilot.example", "Priya Nair (buyer)", Roles.Approver),
            new("rahul.mehta@invoicepilot.example", "Rahul Mehta (buyer)", Roles.Approver),
            new("ap.manager@invoicepilot.example", "AP Manager", Roles.Approver),
            new("finance.controller@invoicepilot.example", "Finance Controller", Roles.Approver),
            new("admin@invoicepilot.example", "Admin", Roles.Admin),
        ];
        // One hash for all demo users: they share the demo password, and startup stays fast.
        var hash = Passwords.Hash(password);
        _users = users.ToDictionary(u => u.Email, u => (u, hash), StringComparer.OrdinalIgnoreCase);
        _dummyHash = hash;
    }

    public IReadOnlyList<AppUser> All => _users.Values.Select(v => v.User).ToList();

    /// <summary>Unknown emails still pay for a hash check, so timing doesn't reveal who has an account.</summary>
    public AppUser? Verify(string email, string password)
    {
        if (_users.TryGetValue(email.Trim(), out var entry))
        {
            return Passwords.Verify(password, entry.Hash) ? entry.User : null;
        }
        Passwords.Verify(password, _dummyHash);
        return null;
    }
}

/// <summary>Every API call needs a signed-in user, unless sign-in is turned off.</summary>
public sealed class SignedInRequirement : IAuthorizationRequirement;

public sealed class SignedInHandler(IOptionsMonitor<AuthOptions> options) : AuthorizationHandler<SignedInRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SignedInRequirement requirement)
    {
        if (!options.CurrentValue.Enabled || context.User.Identity?.IsAuthenticated == true) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>The person acting, as the review rules see them.</summary>
public sealed record Reviewer(string Email, string Role, bool Enforced)
{
    public static Reviewer From(HttpContext http, AuthOptions options)
    {
        if (!options.Enabled || http.User.Identity?.IsAuthenticated != true) return new("clerk", Roles.Admin, false);
        return new(http.User.Identity.Name ?? "unknown", http.User.FindFirstValue(ClaimTypes.Role) ?? Roles.Clerk, true);
    }
}

public sealed record SignInRequest(string Email, string Password);

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/api/account").WithTags("Account");

        account.MapPost("/signin", async (SignInRequest request, UserDirectory users, HttpContext http) =>
        {
            var user = string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password)
                ? null
                : users.Verify(request.Email, request.Password);
            if (user is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed",
                    detail: "The email or password is wrong.");
            }
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, user.Email),
                new Claim(ClaimTypes.GivenName, user.Name),
                new Claim(ClaimTypes.Role, user.Role),
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Ok(user);
        }).AllowAnonymous().RequireRateLimiting("signin").DisableAntiforgery();

        account.MapPost("/signout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous().DisableAntiforgery();

        account.MapGet("/me", (HttpContext http, IOptions<AuthOptions> options) =>
        {
            if (!options.Value.Enabled) return Results.Ok(new AppUser("clerk", "Local user (sign-in off)", Roles.Admin));
            if (http.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            return Results.Ok(new AppUser(http.User.Identity.Name!, http.User.FindFirstValue(ClaimTypes.GivenName) ?? "",
                http.User.FindFirstValue(ClaimTypes.Role) ?? Roles.Clerk));
        }).AllowAnonymous();

        return app;
    }
}
