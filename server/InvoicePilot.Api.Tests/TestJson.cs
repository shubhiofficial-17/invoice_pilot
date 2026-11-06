using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvoicePilot.Api.Tests;

/// <summary>The same JSON settings the API uses (web defaults, enums as camelCase strings).</summary>
public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
