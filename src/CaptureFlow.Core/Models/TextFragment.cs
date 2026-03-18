namespace CaptureFlow.Core.Models;

public enum TextSource
{
    Native,
    Ocr
}

/// <summary>
/// A fragment of text with its bounding box in normalised coordinates.
/// </summary>
public class TextFragment
{
    public required string Text { get; init; }
    public required NormalisedRect Bounds { get; init; }
    public TextSource Source { get; init; } = TextSource.Native;
    public double Confidence { get; init; } = 1.0;
    public int PageIndex { get; init; }
}
