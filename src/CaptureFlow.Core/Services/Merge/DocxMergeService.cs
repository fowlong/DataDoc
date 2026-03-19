using System.Text.RegularExpressions;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using CaptureFlow.Core.Services.Adapters;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CaptureFlow.Core.Services.Merge;

public sealed class DocxMergeService : IMergeService
{
    private static readonly Regex PlaceholderRegex = new(
        @"\{\{(\w+)\}\}",
        RegexOptions.Compiled);

    private readonly DocumentAdapterFactory _adapterFactory;
    private readonly ILogger<DocxMergeService> _logger;

    public DocxMergeService(DocumentAdapterFactory adapterFactory, ILogger<DocxMergeService> logger)
    {
        _adapterFactory = adapterFactory;
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

        foreach (var headerPart in mainPart?.HeaderParts ?? Enumerable.Empty<HeaderPart>())
        {
            if (headerPart.Header is not null)
                ScanElementForPlaceholders(headerPart.Header, placeholders);
        }

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

    public async Task<byte[]> GeneratePreviewAsync(
        string templatePath,
        Dictionary<string, string> fieldValues,
        MergeOutputFormat format,
        int pageIndex = 0,
        List<MergeAnnotation>? annotations = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("Template file not found.", templatePath);

        ct.ThrowIfCancellationRequested();

        var templateBytes = File.ReadAllBytes(templatePath);
        var mergedBytes = MergeDocument(templateBytes, fieldValues, ct);

        // Save merged DOCX to temp file, render to PNG via adapter
        var tempPath = Path.Combine(Path.GetTempPath(), $"merge_preview_{Guid.NewGuid()}.docx");
        try
        {
            await File.WriteAllBytesAsync(tempPath, mergedBytes, ct);

            var adapter = _adapterFactory.GetAdapter(tempPath);
            if (adapter == null)
                throw new InvalidOperationException("No adapter found for DOCX rendering.");

            var doc = await adapter.LoadAsync(tempPath, ct);
            pageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, doc.Pages.Count - 1));

            var pngBytes = await adapter.RenderPageAsync(doc, pageIndex, 800, ct);

            // Draw annotations on the PNG if any exist for this page
            if (annotations?.Count > 0)
                pngBytes = ApplyAnnotationsToImage(pngBytes, annotations, pageIndex);

            _logger.LogInformation("Generated DOCX preview for page {PageIndex}.", pageIndex);
            return pngBytes;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public async Task<int> GetPreviewPageCountAsync(
        string templatePath,
        Dictionary<string, string> fieldValues,
        CancellationToken ct = default)
    {
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("Template file not found.", templatePath);

        var templateBytes = File.ReadAllBytes(templatePath);
        var mergedBytes = MergeDocument(templateBytes, fieldValues, ct);

        var tempPath = Path.Combine(Path.GetTempPath(), $"merge_pagecount_{Guid.NewGuid()}.docx");
        try
        {
            await File.WriteAllBytesAsync(tempPath, mergedBytes, ct);
            var adapter = _adapterFactory.GetAdapter(tempPath);
            if (adapter == null) return 1;

            var doc = await adapter.LoadAsync(tempPath, ct);
            return doc.Pages.Count;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public Task<List<string>> GenerateBulkAsync(
        string templatePath,
        List<Dictionary<string, string>> rows,
        MergeOutputFormat format,
        string outputDirectory,
        string fileNamePattern,
        List<MergeAnnotation>? annotations = null,
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

            // Apply annotations to the DOCX if any
            if (annotations?.Count > 0)
                outputBytes = ApplyAnnotationsToDocx(outputBytes, annotations);

            var fileName = ResolveFileNamePattern(fileNamePattern, row, i + 1);

            if (!fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                fileName += ".docx";

            var outputPath = Path.Combine(outputDirectory, fileName);
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

        foreach (var headerPart in mainPart?.HeaderParts ?? Enumerable.Empty<HeaderPart>())
        {
            if (headerPart.Header is not null)
            {
                ReplacePlaceholdersInElement(headerPart.Header, fieldValues);
                headerPart.Header.Save();
            }
        }

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

    private static byte[] ApplyAnnotationsToDocx(byte[] docxBytes, List<MergeAnnotation> annotations)
    {
        using var ms = new MemoryStream();
        ms.Write(docxBytes, 0, docxBytes.Length);
        ms.Position = 0;

        using var document = WordprocessingDocument.Open(ms, true);
        var mainPart = document.MainDocumentPart;
        var body = mainPart?.Document?.Body;
        if (body == null) return docxBytes;

        // Add annotations as paragraphs with positioning information
        // Group by page for organization
        var pageGroups = annotations
            .Where(a => !string.IsNullOrWhiteSpace(a.Text))
            .GroupBy(a => a.PageIndex)
            .OrderBy(g => g.Key);

        foreach (var group in pageGroups)
        {
            foreach (var annotation in group)
            {
                // Add annotation text as a paragraph with a visual indicator
                var run = new Run(
                    new RunProperties(
                        new Color { Val = "2563EB" },
                        new FontSize { Val = ((int)(annotation.FontSize * 2)).ToString() }
                    ),
                    new Text(annotation.Text) { Space = SpaceProcessingModeValues.Preserve }
                );

                var paragraph = new Paragraph(
                    new ParagraphProperties(
                        new ParagraphBorders(
                            new LeftBorder
                            {
                                Val = BorderValues.Single,
                                Color = "2563EB",
                                Size = 4,
                                Space = 4
                            }
                        )
                    ),
                    run
                );

                body.AppendChild(paragraph);
            }
        }

        mainPart?.Document?.Save();
        document.Dispose();

        return ms.ToArray();
    }

    internal static byte[] ApplyAnnotationsToImage(byte[] pngBytes, List<MergeAnnotation> annotations, int pageIndex)
    {
        var pageAnnotations = annotations
            .Where(a => a.PageIndex == pageIndex && !string.IsNullOrWhiteSpace(a.Text))
            .ToList();

        if (pageAnnotations.Count == 0)
            return pngBytes;

        using var bitmap = SKBitmap.Decode(pngBytes);
        if (bitmap == null) return pngBytes;

        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(bitmap, 0, 0);

        using var textPaint = new SKPaint
        {
            Color = new SKColor(37, 99, 235), // Blue
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };

        using var bgPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 200),
            Style = SKPaintStyle.Fill
        };

        foreach (var annotation in pageAnnotations)
        {
            float x = (float)(annotation.NormX * bitmap.Width);
            float y = (float)(annotation.NormY * bitmap.Height);
            textPaint.TextSize = (float)annotation.FontSize * (bitmap.Width / 800f);

            var textBounds = new SKRect();
            textPaint.MeasureText(annotation.Text, ref textBounds);

            // Draw semi-transparent background
            canvas.DrawRect(x - 2, y - textBounds.Height - 2,
                textBounds.Width + 4, textBounds.Height + 4, bgPaint);

            canvas.DrawText(annotation.Text, x, y, textPaint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static void ScanElementForPlaceholders(OpenXmlElement element, HashSet<string> placeholders)
    {
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

            var fullText = string.Concat(runs.SelectMany(r => r.Descendants<Text>()).Select(t => t.Text));

            if (!PlaceholderRegex.IsMatch(fullText))
                continue;

            var replacedText = PlaceholderRegex.Replace(fullText, match =>
            {
                var key = match.Groups[1].Value;
                return fieldValues.TryGetValue(key, out var value) ? value : match.Value;
            });

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
