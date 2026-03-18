using System.Text.RegularExpressions;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace CaptureFlow.Core.Services.Merge;

/// <summary>
/// PDF merge service. PdfPig is a read-only PDF library, so full form-filling
/// is not supported in v1. This service extracts placeholder names from PDF text
/// content for mapping purposes, but generated output is always DOCX-based.
/// For production PDF form filling, a read-write PDF library (e.g. iTextSharp,
/// PdfSharp, or QuestPDF) would be needed.
/// </summary>
public sealed class PdfMergeService : IMergeService
{
    private static readonly Regex PlaceholderRegex = new(
        @"\{\{(\w+)\}\}",
        RegexOptions.Compiled);

    private readonly ILogger<PdfMergeService> _logger;

    public PdfMergeService(ILogger<PdfMergeService> logger)
    {
        _logger = logger;
    }

    public Task<List<string>> GetTemplatePlaceholdersAsync(string templatePath, CancellationToken ct = default)
    {
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("Template file not found.", templatePath);

        ct.ThrowIfCancellationRequested();

        var placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var document = PdfDocument.Open(templatePath);

        // Scan text content for {{placeholder}} tokens
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            var pageText = page.Text;
            foreach (Match match in PlaceholderRegex.Matches(pageText))
            {
                placeholders.Add(match.Groups[1].Value);
            }
        }

        // Check for AcroForm fields (PdfPig exposes form data on pages)
        // PdfPig can read form field names from the document catalog
        try
        {
            if (document.Structure?.Catalog is not null)
            {
                // Attempt to find form fields via page annotations
                foreach (var page in document.GetPages())
                {
                    // PdfPig doesn't directly expose AcroForm fields in a simple API,
                    // but we can detect form-like annotations. For v1, we rely on text scanning.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not inspect PDF form structure; relying on text scanning.");
        }

        if (placeholders.Count == 0)
        {
            _logger.LogWarning(
                "No {{placeholders}} found in PDF template {TemplatePath}. " +
                "PDF merge is limited in v1 - DOCX templates are recommended for mail-merge workflows.",
                templatePath);
        }
        else
        {
            _logger.LogInformation(
                "Found {Count} placeholders in PDF template {TemplatePath}.",
                placeholders.Count, templatePath);
        }

        return Task.FromResult(placeholders.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task<byte[]> GeneratePreviewAsync(
        string templatePath,
        Dictionary<string, string> fieldValues,
        MergeOutputFormat format,
        CancellationToken ct = default)
    {
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("Template file not found.", templatePath);

        ct.ThrowIfCancellationRequested();

        _logger.LogWarning(
            "PDF merge preview is limited in v1. PdfPig is read-only and cannot modify PDF files. " +
            "Returning the original template bytes. For full merge support, use DOCX templates.");

        // In v1, we cannot write/modify PDFs with PdfPig alone.
        // Return the original template as-is with a warning.
        // A future version could use a PDF writing library (iText, PdfSharp, QuestPDF)
        // to create output with text overlays at field positions.
        var templateBytes = File.ReadAllBytes(templatePath);
        return Task.FromResult(templateBytes);
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
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("Template file not found.", templatePath);

        _logger.LogWarning(
            "PDF bulk merge is not fully supported in v1. PdfPig is a read-only library and cannot " +
            "produce modified PDF output. Use DOCX templates for mail-merge workflows. " +
            "Copying template to output directory for each row as a placeholder.");

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var templateBytes = File.ReadAllBytes(templatePath);
        var generatedFiles = new List<string>();

        for (int i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var row = rows[i];
            var fileName = ResolveFileNamePattern(fileNamePattern, row, i + 1);

            if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                fileName += ".pdf";

            var outputPath = Path.Combine(outputDirectory, fileName);
            outputPath = GetUniqueFilePath(outputPath);

            // In v1, copy the template as-is since we cannot modify PDF content.
            File.WriteAllBytes(outputPath, templateBytes);
            generatedFiles.Add(outputPath);

            progress?.Report(i + 1);

            _logger.LogDebug(
                "Copied PDF template as document {Index}/{Total}: {OutputPath} (merge not applied).",
                i + 1, rows.Count, outputPath);
        }

        _logger.LogInformation(
            "PDF bulk operation complete. Created {Count} files in {OutputDirectory}. " +
            "Note: field values were NOT merged into PDF output (v1 limitation).",
            generatedFiles.Count, outputDirectory);

        return Task.FromResult(generatedFiles);
    }

    private static string ResolveFileNamePattern(
        string pattern,
        Dictionary<string, string> fieldValues,
        int rowNumber)
    {
        var result = Regex.Replace(
            pattern,
            @"\{\{RowNumber\}\}",
            rowNumber.ToString(),
            RegexOptions.IgnoreCase);

        result = PlaceholderRegex.Replace(result, match =>
        {
            var key = match.Groups[1].Value;
            if (fieldValues.TryGetValue(key, out var value))
                return SanitizeFileName(value);
            return match.Value;
        });

        return result;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    private static string GetUniqueFilePath(string filePath)
    {
        if (!File.Exists(filePath))
            return filePath;

        var directory = Path.GetDirectoryName(filePath)!;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);
        int counter = 1;

        string newPath;
        do
        {
            newPath = Path.Combine(directory, $"{nameWithoutExt}_{counter}{extension}");
            counter++;
        } while (File.Exists(newPath));

        return newPath;
    }
}
