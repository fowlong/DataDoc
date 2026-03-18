namespace CaptureFlow.Core.Models;

/// <summary>
/// Normalised representation of a single page from any document type.
/// </summary>
public class DocumentPage
{
    public int PageIndex { get; init; }

    /// <summary>Original page width in points (or pixels for images).</summary>
    public double OriginalWidth { get; init; }

    /// <summary>Original page height in points (or pixels for images).</summary>
    public double OriginalHeight { get; init; }

    /// <summary>Aspect ratio for rendering.</summary>
    public double AspectRatio => OriginalWidth / OriginalHeight;

    /// <summary>Native text fragments with positioned bounding boxes.</summary>
    public List<TextFragment> NativeTextFragments { get; init; } = [];

    /// <summary>OCR text fragments (populated on demand).</summary>
    public List<TextFragment> OcrTextFragments { get; set; } = [];

    /// <summary>Whether this page has native/selectable text.</summary>
    public bool HasNativeText => NativeTextFragments.Count > 0;

    /// <summary>Whether OCR has been performed on this page.</summary>
    public bool OcrPerformed { get; set; }

    /// <summary>Preview image bytes (PNG). Rendered on demand.</summary>
    public byte[]? PreviewImagePng { get; set; }

    /// <summary>Plain text content for fallback display.</summary>
    public string? PlainText { get; set; }

    /// <summary>All text fragments matching extraction mode.</summary>
    public IEnumerable<TextFragment> GetTextFragments(ExtractionMode mode) => mode switch
    {
        ExtractionMode.NativeOnly => NativeTextFragments,
        ExtractionMode.OcrOnly => OcrTextFragments,
        ExtractionMode.NativeWithOcrFallback => HasNativeText ? NativeTextFragments : OcrTextFragments,
        ExtractionMode.Both => NativeTextFragments.Concat(OcrTextFragments),
        _ => NativeTextFragments
    };
}
