using System.Text;
using UglyToad.PdfPig;

namespace InvoicePilot.Api.Extraction;

/// <summary>Pulls the text layer out of a PDF, line by line, in reading order.</summary>
public static class PdfText
{
    public const int MaxPages = 10;

    /// <returns>The text, or an empty string for a scanned PDF with no text layer.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a readable PDF.</exception>
    public static string Extract(byte[] pdf)
    {
        try
        {
            using var document = PdfDocument.Open(pdf);
            var text = new StringBuilder();
            foreach (var page in document.GetPages().Take(MaxPages))
            {
                // Group words into lines by their baseline (rounded to absorb small jitter),
                // top of the page first, then left to right.
                var lines = page.GetWords()
                    .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3.0))
                    .OrderByDescending(g => g.Key)
                    .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
                foreach (var line in lines) text.AppendLine(line);
                text.AppendLine();
            }
            return text.ToString().Trim();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidDataException("The PDF could not be read.", e);
        }
    }
}
