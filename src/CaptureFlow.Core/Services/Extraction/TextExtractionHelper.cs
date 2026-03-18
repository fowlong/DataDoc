using CaptureFlow.Core.Models;

namespace CaptureFlow.Core.Services.Extraction;

/// <summary>
/// Static helper that extracts text from a <see cref="DocumentPage"/> within a normalised rectangle.
/// Fragments are sorted top-to-bottom then left-to-right and joined with appropriate whitespace.
/// </summary>
public static class TextExtractionHelper
{
    private const double LineMergeThreshold = 0.008;

    /// <summary>
    /// Extracts text from fragments that intersect <paramref name="rect"/> on the given page,
    /// using the specified extraction mode to choose native vs OCR fragments.
    /// </summary>
    public static string ExtractText(DocumentPage page, NormalisedRect rect, ExtractionMode mode)
    {
        var fragments = page.GetTextFragments(mode)
            .Where(f => f.Bounds.Intersects(rect))
            .OrderBy(f => f.Bounds.Y)
            .ThenBy(f => f.Bounds.X)
            .ToList();

        if (fragments.Count == 0)
            return string.Empty;

        return JoinFragments(fragments);
    }

    /// <summary>
    /// Returns text fragments that intersect the given rectangle, sorted reading-order.
    /// </summary>
    public static List<TextFragment> GetIntersectingFragments(
        DocumentPage page, NormalisedRect rect, ExtractionMode mode)
    {
        return page.GetTextFragments(mode)
            .Where(f => f.Bounds.Intersects(rect))
            .OrderBy(f => f.Bounds.Y)
            .ThenBy(f => f.Bounds.X)
            .ToList();
    }

    /// <summary>
    /// Computes the average confidence of fragments intersecting the rectangle.
    /// Returns 1.0 when no fragments are found.
    /// </summary>
    public static double ComputeConfidence(DocumentPage page, NormalisedRect rect, ExtractionMode mode)
    {
        var fragments = page.GetTextFragments(mode)
            .Where(f => f.Bounds.Intersects(rect))
            .ToList();

        return fragments.Count == 0 ? 1.0 : fragments.Average(f => f.Confidence);
    }

    /// <summary>
    /// Joins a sorted list of text fragments using spaces within a line and newlines between lines.
    /// Two fragments are considered on the same line if their Y coordinates differ by less than
    /// <see cref="LineMergeThreshold"/>.
    /// </summary>
    private static string JoinFragments(List<TextFragment> fragments)
    {
        if (fragments.Count == 0)
            return string.Empty;

        var lines = new List<List<TextFragment>>();
        var currentLine = new List<TextFragment> { fragments[0] };
        double currentY = fragments[0].Bounds.Y;

        for (int i = 1; i < fragments.Count; i++)
        {
            var frag = fragments[i];
            if (Math.Abs(frag.Bounds.Y - currentY) <= LineMergeThreshold)
            {
                currentLine.Add(frag);
            }
            else
            {
                lines.Add(currentLine);
                currentLine = [frag];
                currentY = frag.Bounds.Y;
            }
        }
        lines.Add(currentLine);

        var lineTexts = lines.Select(line =>
            string.Join(" ", line.OrderBy(f => f.Bounds.X).Select(f => f.Text)));

        return string.Join(Environment.NewLine, lineTexts);
    }
}
