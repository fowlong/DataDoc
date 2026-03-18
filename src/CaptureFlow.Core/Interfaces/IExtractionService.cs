using CaptureFlow.Core.Models;

namespace CaptureFlow.Core.Interfaces;

public interface IExtractionService
{
    Task<List<ExtractionRow>> ExtractAsync(
        SourceDocument document,
        IReadOnlyList<CaptureBox> captureBoxes,
        IReadOnlyList<RepeatGroup> repeatGroups,
        CancellationToken ct = default);
}

public interface IMergeService
{
    Task<List<string>> GetTemplatePlaceholdersAsync(string templatePath, CancellationToken ct = default);
    Task<byte[]> GeneratePreviewAsync(string templatePath, Dictionary<string, string> fieldValues, MergeOutputFormat format, CancellationToken ct = default);
    Task<List<string>> GenerateBulkAsync(string templatePath, List<Dictionary<string, string>> rows, MergeOutputFormat format, string outputDirectory, string fileNamePattern, IProgress<int>? progress = null, CancellationToken ct = default);
}

public interface IBatchProcessor
{
    Task<List<ExtractionRow>> ProcessBatchAsync(
        IReadOnlyList<string> filePaths,
        DocumentTemplate template,
        IReadOnlyList<PageTemplate> pageTemplates,
        IProgress<BatchProgress>? progress = null,
        CancellationToken ct = default);
}

public class BatchProgress
{
    public int TotalFiles { get; init; }
    public int ProcessedFiles { get; set; }
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public string? CurrentFile { get; set; }
    public string? CurrentStatus { get; set; }
    public List<BatchError> Errors { get; set; } = [];
}

public class BatchError
{
    public required string FilePath { get; init; }
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
}
