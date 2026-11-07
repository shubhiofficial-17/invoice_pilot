using System.Runtime.CompilerServices;

namespace InvoicePilot.Api.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Most tests exercise the pipeline, not sign-in, so they run with sign-in off.
    /// AuthTests turn it back on for their own host.
    /// </summary>
    [ModuleInitializer]
    internal static void DisableSignInByDefault() => Environment.SetEnvironmentVariable("Auth__Enabled", "false");
}
