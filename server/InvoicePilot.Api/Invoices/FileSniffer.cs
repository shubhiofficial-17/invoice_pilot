namespace InvoicePilot.Api.Invoices;

/// <summary>
/// Decides a file's type from its first bytes. The Content-Type a browser sends is
/// whatever the client says, so it is never trusted.
/// </summary>
public static class FileSniffer
{
    public const string Pdf = "application/pdf";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

    public static string? Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(PdfMagic)) return Pdf;
        if (head.StartsWith(PngMagic)) return Png;
        if (head.StartsWith(JpegMagic)) return Jpeg;
        return null;
    }

    /// <summary>Strips any path, control characters and quotes, and caps the length.</summary>
    public static string SafeFileName(string? name, string contentType)
    {
        var baseName = Path.GetFileName((name ?? "").Replace('\\', '/'));
        var clean = new string(baseName.Where(c => !char.IsControl(c) && c is not '"' and not '/').ToArray()).Trim();
        if (clean.Length > 120) clean = clean[^120..];
        if (clean.Length == 0 || clean.Trim('.').Length == 0)
        {
            clean = "invoice" + contentType switch { Pdf => ".pdf", Png => ".png", _ => ".jpg" };
        }
        return clean;
    }
}
