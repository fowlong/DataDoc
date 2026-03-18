using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaptureFlow.Core.Services.Extraction;

/// <summary>
/// Processes a list of files in parallel using document adapters, applies templates,
/// and extracts data with configurable concurrency and progress reporting.
/// </summary>
public class BatchProcessor : IBatchProcessor
{
    private readonly IExtractionService _extractionService;
    private readonly IReadOnlyList<IDocumentAdapter> _adapters;
    private readonly ILogger<BatchProcessor> _logger;
    private readonly int _maxConcurrency;

    public BatchProcessor(
        IExtractionService extractionService,
        IEnumerable<IDocumentAdapter> adapters,
        ILogger<BatchProcessor> logger,
        int maxConcurrency = 4)
    {
        _extractionService = extractionService;
        _adapters = adapters.ToList();
        _logger = logger;
        _maxConcurrency = Math.Max(1, maxConcurrency);
    }

    public async Task<List<ExtractionRow>> ProcessBatchAsync(
        IReadOnlyList<string> filePaths,
        DocumentTemplate template,
        IReadOnlyList<PageTemplate> pageTemplates,
        IProgress<BatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var batchProgress = new BatchProgress
        {
            TotalFiles = filePaths.Count,
            ProcessedFiles = 0,
            SuccessCount = 0,
            ErrorCount = 0
        };

        var allRows = new List<ExtractionRow>();
        var lockObj = new object();

        _logger.LogInformation("Starting batch processing of {FileCount} files with concurrency {Concurrency}",
            filePaths.Count, _maxConcurrency);

        using var semaphore = new SemaphoreSlim(_maxConcurrency);
        var tasks = filePaths.Select(filePath => ProcessFileAsync(
            filePath, template, pageTemplates, semaphore, batchProgress, allRows, lockObj, progress, ct));

        await Task.WhenAll(tasks);

        _logger.LogInformation(
            "Batch processing complete: {Success} succeeded, {Errors} failed, {RowCount} total rows",
            batchProgress.SuccessCount, batchProgress.ErrorCount, allRows.Count);

        return allRows;
    }

    private async Task ProcessFileAsync(
        string filePath,
        DocumentTemplate template,
        IReadOnlyList<PageTemplate> pageTemplates,
        SemaphoreSlim semaphore,
        BatchProgress batchProgress,
        List<ExtractionRow> allRows,
        object lockObj,
        IProgress<BatchProgress>? progress,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            lock (lockObj)
            {
                batchProgress.CurrentFile = Path.GetFileName(filePath);
                batchProgress.CurrentStatus = "Loading";
                progress?.Report(batchProgress);
            }

            var adapter = FindAdapter(filePath);
            if (adapter == null)
            {
                RecordError(batchProgress, lockObj, filePath,
                    $"No adapter found for file type: {Path.GetExtension(filePath)}", progress);
                return;
            }

            var document = await adapter.LoadAsync(filePath, ct);
            if (document.State == ProcessingState.Error)
            {
                RecordError(batchProgress, lockObj, filePath,
                    document.ErrorMessage ?? "Failed to load document", progress);
                return;
            }

            lock (lockObj)
            {
                batchProgress.CurrentStatus = "Extracting";
                progress?.Report(batchProgress);
            }

            // Gather capture boxes and repeat groups from matching page templates and document-level fields.
            var captureBoxes = new List<CaptureBox>(template.DocumentLevelFields);
            var repeatGroups = new List<RepeatGroup>(template.RepeatGroups);

            foreach (var assignment in template.PageAssignments)
            {
                var pageTemplate = pageTemplates.FirstOrDefault(pt => pt.Id == assignment.PageTemplateId);
                if (pageTemplate == null) continue;

                captureBoxes.AddRange(pageTemplate.CaptureBoxes);
                repeatGroups.AddRange(pageTemplate.RepeatGroups);
            }

            var rows = await _extractionService.ExtractAsync(document, captureBoxes, repeatGroups, ct);

            lock (lockObj)
            {
                allRows.AddRange(rows);
                batchProgress.ProcessedFiles++;
                batchProgress.SuccessCount++;
                batchProgress.CurrentStatus = "Complete";
                progress?.Report(batchProgress);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordError(batchProgress, lockObj, filePath, ex.Message, progress, ex);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private IDocumentAdapter? FindAdapter(string filePath)
    {
        return _adapters.FirstOrDefault(a => a.CanHandle(filePath));
    }

    private static void RecordError(
        BatchProgress batchProgress,
        object lockObj,
        string filePath,
        string message,
        IProgress<BatchProgress>? progress,
        Exception? exception = null)
    {
        lock (lockObj)
        {
            batchProgress.ProcessedFiles++;
            batchProgress.ErrorCount++;
            batchProgress.CurrentStatus = "Error";
            batchProgress.Errors.Add(new BatchError
            {
                FilePath = filePath,
                Message = message,
                Exception = exception
            });
            progress?.Report(batchProgress);
        }
    }
}
