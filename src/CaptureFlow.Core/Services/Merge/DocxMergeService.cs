using System.Text.RegularExpressions;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;

namespace CaptureFlow.Core.Services.Merge;

public sealed class DocxMergeService : IMergeService
{
    private static readonly Regex PlaceholderRegex = new(
        @"\{\{(\w+)\}\}",
        RegexOptions.Compiled);

    private readonly ILogger<DocxMergeService> _logger;

    public DocxMergeService(ILogger<DocxMergeService> logger)
    {
        _logger = logger;
    }

    public Task<List<string>> GetTemplatePlaceholdersAsync(string templatePath, CancellationToken ct = default)
    {
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("Template file not found.", templatePath);

        ct.ThrowIfCancellationRequested();

        var placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var document = WordprocessingDocument.Open(templatePath, false);
        var mainPart = document.MainDocumentPart;

        if (mainPart?.Document?.Body is not null)
        {
            ScanElementForPlaceholders(mainPart.Document.Body, placeholders);
        }

        // Scan headers
        foreach (var headerPart in mainPart?.HeaderParts ?? Enumerable.Empty<HeaderPart>())
        {
            if (headerPart.Header is not null)
                ScanElementForPlaceholders(headerPart.Header, placeholders);
        }

        // Scan footers
        foreach (var footerPart in mainPart?.FooterParts ?? Enumerable.Empty<FooterPart>())
        {
            if (footerPart.Footer is not null)
                ScanElementForPlaceholders(footerPart.Footer, placeholders);
        }

        _logger.LogInformation(
            "Found {Count} placeholders in template {TemplatePath}.",
            placeholders.Count, templatePath);

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

        var templateBytes = File.ReadAllBytes(templatePath);
        var outputBytes = MergeDocument(templateBytes, fieldValues, ct);

        _logger.LogInformation("Generated preview for template {TemplatePath}.", templatePath);

        return Task.FromResult(outputBytes);
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

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var templateBytes = File.ReadAllBytes(templatePath);
        var generatedFiles = new List<string>();

        for (int i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var row = rows[i];
            var outputBytes = MergeDocument(templateBytes, row, ct);

            var fileName = ResolveFileNamePattern(fileNamePattern, row, i + 1);

            // Ensure correct extension
            if (!fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                fileName += ".docx";

            var outputPath = Path.Combine(outputDirectory, fileName);

            // Ensure we don't overwrite by appending a suffix if needed
            outputPath = GetUniqueFilePath(outputPath);

            File.WriteAllBytes(outputPath, outputBytes);
            generatedFiles.Add(outputPath);

            progress?.Report(i + 1);

            _logger.LogDebug(
                "Generated document {Index}/{Total}: {OutputPath}",
                i + 1, rows.Count, outputPath);
        }

        _logger.LogInformation(
            "Bulk merge complete. Generated {Count} documents in {OutputDirectory}.",
            generatedFiles.Count, outputDirectory);

        return Task.FromResult(generatedFiles);
    }

    private byte[] MergeDocument(
        byte[] templateBytes,
        Dictionary<string, string> fieldValues,
        CancellationToken ct)
    {
        using var memoryStream = new MemoryStream();
        memoryStream.Write(templateBytes, 0, templateBytes.Length);
        memoryStream.Position = 0;

        using var document = WordprocessingDocument.Open(memoryStream, true);
        var mainPart = document.MainDocumentPart;

        if (mainPart?.Document?.Body is not null)
        {
            ReplacePlaceholdersInElement(mainPart.Document.Body, fieldValues);
        }

        // Replace in headers
        foreach (var headerPart in mainPart?.HeaderParts ?? Enumerable.Empty<HeaderPart>())
        {
            if (headerPart.Header is not null)
            {
                ReplacePlaceholdersInElement(headerPart.Header, fieldValues);
                headerPart.Header.Save();
            }
        }

        // Replace in footers
        foreach (var footerPart in mainPart?.FooterParts ?? Enumerable.Empty<FooterPart>())
        {
            if (footerPart.Footer is not null)
            {
                ReplacePlaceholdersInElement(footerPart.Footer, fieldValues);
                footerPart.Footer.Save();
            }
        }

        mainPart?.Document?.Save();
        document.Dispose();

        return memoryStream.ToArray();
    }

    private static void ScanElementForPlaceholders(OpenXmlElement element, HashSet<string> placeholders)
    {
        // Collect text from paragraphs. Placeholders may span multiple runs,
        // so we concatenate all run texts within each paragraph and scan the result.
        foreach (var paragraph in element.Descendants<Paragraph>())
        {
            var fullText = string.Concat(paragraph.Descendants<Text>().Select(t => t.Text));
            foreach (Match match in PlaceholderRegex.Matches(fullText))
            {
                placeholders.Add(match.Groups[1].Value);
            }
        }
    }

    private static void ReplacePlaceholdersInElement(
        OpenXmlElement element,
        Dictionary<string, string> fieldValues)
    {
        foreach (var paragraph in element.Descendants<Paragraph>().ToList())
        {
            var runs = paragraph.Descendants<Run>().ToList();
            if (runs.Count == 0)
                continue;

            // Build the full paragraph text from all runs
            var fullText = string.Concat(runs.SelectMany(r => r.Descendants<Text>()).Select(t => t.Text));

            if (!PlaceholderRegex.IsMatch(fullText))
                continue;

            // Replace all placeholders
            var replacedText = PlaceholderRegex.Replace(fullText, match =>
            {
                var key = match.Groups[1].Value;
                return fieldValues.TryGetValue(key, out var value) ? value : match.Value;
            });

            // Consolidate into the first run with text, remove text from subsequent runs
            bool firstTextSet = false;
            foreach (var run in runs)
            {
                var textElements = run.Descendants<Text>().ToList();
                foreach (var textElement in textElements)
                {
                    if (!firstTextSet)
                    {
                        textElement.Text = replacedText;
                        textElement.Space = SpaceProcessingModeValues.Preserve;
                        firstTextSet = true;
                    }
                    else
                    {
                        textElement.Text = string.Empty;
                    }
                }
            }
        }
    }

    private static string ResolveFileNamePattern(
        string pattern,
        Dictionary<string, string> fieldValues,
        int rowNumber)
    {
        // Replace {{RowNumber}} token
        var result = Regex.Replace(
            pattern,
            @"\{\{RowNumber\}\}",
            rowNumber.ToString(),
            RegexOptions.IgnoreCase);

        // Replace any {{FieldName}} tokens with corresponding field values
        result = PlaceholderRegex.Replace(result, match =>
        {
            var key = match.Groups[1].Value;
            if (fieldValues.TryGetValue(key, out var value))
            {
                // Sanitize the value for use in file names
                return SanitizeFileName(value);
            }
            return match.Value;
        });

        return result;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        return sanitized.Trim();
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
