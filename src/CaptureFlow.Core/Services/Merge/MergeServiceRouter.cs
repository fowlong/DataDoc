using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaptureFlow.Core.Services.Merge;

/// <summary>
/// Routes merge operations to the appropriate service based on template file extension.
/// Register this as the <see cref="IMergeService"/> implementation in DI.
/// </summary>
public sealed class MergeServiceRouter : IMergeService
{
    private readonly DocxMergeService _docxService;
    private readonly PdfMergeService _pdfService;
    private readonly ILogger<MergeServiceRouter> _logger;

    public MergeServiceRouter(
        DocxMergeService docxService,
        PdfMergeService pdfService,
        ILogger<MergeServiceRouter> logger)
    {
        _docxService = docxService;
        _pdfService = pdfService;
        _logger = logger;
    }

    public Task<List<string>> GetTemplatePlaceholdersAsync(string templatePath, CancellationToken ct = default)
    {
        var service = ResolveService(templatePath);
        return service.GetTemplatePlaceholdersAsync(templatePath, ct);
    }

    public Task<byte[]> GeneratePreviewAsync(
        string templatePath,
        Dictionary<string, string> fieldValues,
        MergeOutputFormat format,
        CancellationToken ct = default)
    {
        var service = ResolveService(templatePath);
        return service.GeneratePreviewAsync(templatePath, fieldValues, format, ct);
    }

    public Task<List<string>> GenerateBulkAsync(
        string templatePath,
        List<Dictionary<string, string>> rows,
        MergeOutputFormat format,
        string outputDirectory,
        string fileNamePattern,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var service = ResolveService(templatePath);
        return service.GenerateBulkAsync(templatePath, rows, format, outputDirectory, fileNamePattern, progress, ct);
    }

    private IMergeService ResolveService(string templatePath)
    {
        var extension = Path.GetExtension(templatePath)?.ToLowerInvariant();

        return extension switch
        {
            ".docx" => _docxService,
            ".pdf" => _pdfService,
            _ => throw new NotSupportedException(
                $"Template file type '{extension}' is not supported for merge operations. " +
                "Supported types: .docx, .pdf")
        };
    }
}
