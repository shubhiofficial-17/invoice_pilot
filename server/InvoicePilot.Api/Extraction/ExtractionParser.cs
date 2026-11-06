using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InvoicePilot.Api.Extraction;

/// <summary>
/// Turns the model's reply into an <see cref="ExtractedInvoice"/>. Models wrap JSON in code
/// fences, write numbers as "₹1,234.50", or skip fields, so parsing is forgiving about form
/// but strict about meaning: anything it can't read becomes null, never a guess.
/// </summary>
public static partial class ExtractionParser
{
    public const int MaxLines = 200;

    /// <exception cref="FormatException">The reply contains no usable JSON object.</exception>
    public static ExtractedInvoice Parse(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) throw new FormatException("The reply contains no JSON object.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(reply[start..(end + 1)], new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException e)
        {
            throw new FormatException($"The reply is not valid JSON: {e.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The reply is not a JSON object.");

            var lines = new List<ExtractedLine>();
            if (root.TryGetProperty("lines", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in arr.EnumerateArray().Take(MaxLines))
                {
                    if (l.ValueKind != JsonValueKind.Object) continue;
                    var description = Str(l, "description") ?? "";
                    var quantity = Dec(l, "quantity");
                    var unitPrice = Dec(l, "unitPrice");
                    var amount = Dec(l, "amount");
                    // A line with neither a quantity/price nor an amount carries no information.
                    if (quantity is null && unitPrice is null && amount is null) continue;
                    lines.Add(new ExtractedLine(
                        description,
                        Str(l, "sku"),
                        quantity ?? 1m,
                        unitPrice ?? amount ?? 0m,
                        Rate(l, "taxRate"),
                        amount));
                }
            }

            return new ExtractedInvoice(
                Str(root, "supplierName"),
                Str(root, "supplierTaxId"),
                Str(root, "invoiceNumber"),
                Date(root, "invoiceDate"),
                Date(root, "dueDate"),
                Str(root, "poNumber"),
                Str(root, "currency")?.ToUpperInvariant(),
                lines,
                Dec(root, "subtotal"),
                Dec(root, "taxTotal"),
                Dec(root, "total"));
        }
    }

    private static string? Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        s = s?.Trim();
        return string.IsNullOrEmpty(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    public static decimal? Dec(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetDecimal(out var d) ? d : null;
        if (v.ValueKind != JsonValueKind.String) return null;
        return ParseAmount(v.GetString());
    }

    /// <summary>"₹ 1,234.50" → 1234.50; "(12.00)" → -12.00. Commas are thousands separators.</summary>
    public static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var negative = text.Contains('(') && text.Contains(')');
        var cleaned = NotNumber().Replace(text, "");
        if (cleaned.Length == 0 || cleaned == "-" || cleaned == ".") return null;
        if (!decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }
        return negative && value > 0 ? -value : value;
    }

    /// <summary>Tax rates as fractions: 18, "18%" and 0.18 all mean 0.18.</summary>
    private static decimal? Rate(JsonElement e, string name)
    {
        var value = Dec(e, name);
        if (value is null) return null;
        return value > 1m ? value / 100m : value;
    }

    private static DateOnly? Date(JsonElement e, string name)
    {
        var s = Str(e, name);
        if (s is null) return null;
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "d MMM yyyy", "dd MMM yyyy", "MMM d, yyyy", "yyyy/MM/dd"];
        return DateOnly.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    [GeneratedRegex(@"[^0-9.\-]")]
    private static partial Regex NotNumber();
}
