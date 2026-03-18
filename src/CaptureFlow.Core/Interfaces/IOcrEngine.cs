using CaptureFlow.Core.Models;

namespace CaptureFlow.Core.Interfaces;

public interface IOcrEngine
{
    Task<List<TextFragment>> RecognizeAsync(byte[] imageData, int pageIndex, double pageWidth, double pageHeight, CancellationToken ct = default);
    bool IsAvailable { get; }
}
