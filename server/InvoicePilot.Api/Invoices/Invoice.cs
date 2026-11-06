using System.Security.Cryptography;

namespace InvoicePilot.Api.Invoices;

public enum InvoiceStatus { Received, Extracting, Extracted, ExtractionFailed }

/// <summary>The uploaded document. ContentType comes from the file's own bytes, never from the client.</summary>
public sealed record InvoiceFile(string FileName, string ContentType, long Size, string Sha256);

/// <summary>
/// One item in the AP inbox. Version increases on every write and is used for
/// optimistic concurrency (the background worker and people may update the same invoice).
/// </summary>
public sealed record Invoice(
    string Id,
    InvoiceStatus Status,
    InvoiceFile File,
    DateTimeOffset ReceivedAt,
    DateTimeOffset UpdatedAt,
    int Version);

public static class InvoiceIds
{
    // No look-alike characters (0/o, 1/l/i), so ids are easy to read out and copy.
    private const string Alphabet = "23456789abcdefghjkmnpqrstuvwxyz";

    /// <summary>"ap_" plus 12 random characters (about 59 bits).</summary>
    public static string New()
    {
        Span<char> chars = stackalloc char[12];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return "ap_" + new string(chars);
    }
}
