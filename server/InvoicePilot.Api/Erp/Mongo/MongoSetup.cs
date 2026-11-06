using MongoDB.Bson.Serialization.Conventions;

namespace InvoicePilot.Api.Erp.Mongo;

public static class MongoSetup
{
    private static int _configured;

    /// <summary>Registers serialization conventions once per process: camelCase fields, tolerate unknown fields.</summary>
    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1) return;

        var pack = new ConventionPack
        {
            new CamelCaseElementNameConvention(),
            new IgnoreExtraElementsConvention(true),
        };
        ConventionRegistry.Register(
            "invoicepilot",
            pack,
            t => t.Namespace?.StartsWith("InvoicePilot", StringComparison.Ordinal) == true);
    }
}
