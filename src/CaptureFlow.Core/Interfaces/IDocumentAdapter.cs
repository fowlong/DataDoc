using CaptureFlow.Core.Models;

namespace CaptureFlow.Core.Interfaces;

public interface IDocumentAdapter
{
    IReadOnlyList<SupportedFileType> SupportedTypes { get; }
    bool CanHandle(string filePath);
    Task<SourceDocument> LoadAsync(string filePath, CancellationToken ct = default);
    Task<byte[]> RenderPageAsync(SourceDocument document, int pageIndex, int widthPx, CancellationToken ct = default);
}
