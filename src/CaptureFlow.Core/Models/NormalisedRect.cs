namespace CaptureFlow.Core.Models;

/// <summary>
/// Rectangle in normalised coordinates (0.0 to 1.0 relative to page dimensions).
/// </summary>
public record NormalisedRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(double px, double py)
        => px >= X && px <= Right && py >= Y && py <= Bottom;

    public bool Intersects(NormalisedRect other)
        => X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;

    public NormalisedRect Clamp()
        => new(
            Math.Max(0, Math.Min(1, X)),
            Math.Max(0, Math.Min(1, Y)),
            Math.Max(0, Math.Min(1 - Math.Max(0, X), Width)),
            Math.Max(0, Math.Min(1 - Math.Max(0, Y), Height))
        );
}
