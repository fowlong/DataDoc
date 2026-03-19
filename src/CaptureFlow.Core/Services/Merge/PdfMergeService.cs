using System.Text.RegularExpressions;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using CaptureFlow.Core.Services.Adapters;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Extensions.Logging;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace CaptureFlow.Core.Services.Merge;

/// <summary>
/// PDF merge service using PdfPig for reading/text extraction and PdfSharpCore for writing.
/// Placeholder replacement uses an overlay approach: white rectangle over placeholder, then replacement text drawn on top.
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

        using var document = UglyToad.PdfPig.PdfDocument.Open(templatePath);

        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            var pageText = page.Text;
            foreach (Match match in PlaceholderRegex.Matches(pageText))
            {
                placeholders.Add(match.Groups[1].Value);
            }
        }

        if (placeholders.Count == 0)
        {
            _logger.LogWarning(
                "No {{placeholders}} found in PDF template {TemplatePath}.",
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

        // Apply placeholder replacements using PdfSharp overlay
        var mergedPdfBytes = ApplyPlaceholderOverlays(templatePath, fieldValues);

        // Save merged PDF to temp, render via Docnet
        var tempPath = Path.Combine(Path.GetTempPath(), $"merge_preview_{Guid.NewGuid()}.pdf");
        try
        {
            await File.WriteAllBytesAsync(tempPath, mergedPdfBytes, ct);

            var pngBytes = await Task.Run(() =>
            {
                using var library = DocLib.Instance;
                using var docReader = library.GetDocReader(tempPath, new PageDimensions(800, 1200));

                int totalPages = docReader.GetPageCount();
                pageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, totalPages - 1));

                using var pageReader = docReader.GetPageReader(pageIndex);
                var rawBytes = pageReader.GetImage();
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();

                if (rawBytes == null || rawBytes.Length == 0)
                    return Array.Empty<byte>();

                return ConvertBgraToSkiaPng(rawBytes, width, height);
            }, ct);

            // Draw annotations on the PNG
            if (annotations?.Count > 0)
                pngBytes = DocxMergeService.ApplyAnnotationsToImage(pngBytes, annotations, pageIndex);

            _logger.LogInformation("Generated PDF preview for page {PageIndex}.", pageIndex);
            return pngBytes;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public Task<int> GetPreviewPageCountAsync(
        string templatePath,
        Dictionary<string, string> fieldValues,
        CancellationToken ct = default)
    {
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("Template file not found.", templatePath);

        using var pdfDoc = UglyToad.PdfPig.PdfDocument.Open(templatePath);
        return Task.FromResult(pdfDoc.NumberOfPages);
    }

    public async Task<List<string>> GenerateBulkAsync(
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

        var generatedFiles = new List<string>();

        for (int i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var row = rows[i];
            var mergedPdfBytes = ApplyPlaceholderOverlays(templatePath, row, annotations);

            var fileName = ResolveFileNamePattern(fileNamePattern, row, i + 1);

            if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                fileName += ".pdf";

            var outputPath = Path.Combine(outputDirectory, fileName);
            outputPath = GetUniqueFilePath(outputPath);

            await File.WriteAllBytesAsync(outputPath, mergedPdfBytes, ct);
            generatedFiles.Add(outputPath);

            progress?.Report(i + 1);

            _logger.LogDebug(
                "Generated PDF document {Index}/{Total}: {OutputPath}",
                i + 1, rows.Count, outputPath);
        }

        _logger.LogInformation(
            "PDF bulk merge complete. Generated {Count} documents in {OutputDirectory}.",
            generatedFiles.Count, outputDirectory);

        return generatedFiles;
    }

    /// <summary>
    /// Applies placeholder overlays to a PDF using PdfSharpCore.
    /// Uses PdfPig to find placeholder positions, then PdfSharp to draw white rectangles + replacement text.
    /// </summary>
    private byte[] ApplyPlaceholderOverlays(
        string templatePath,
        Dictionary<string, string> fieldValues,
        List<MergeAnnotation>? annotations = null)
    {
        // First, find placeholder positions using PdfPig
        var placeholderPositions = FindPlaceholderPositions(templatePath);

        // Open with PdfSharp for modification
        using var pdfDoc = PdfReader.Open(templatePath, PdfDocumentOpenMode.Modify);

        for (int pageIdx = 0; pageIdx < pdfDoc.PageCount; pageIdx++)
        {
            var page = pdfDoc.Pages[pageIdx];
            var pageWidth = page.Width.Point;
            var pageHeight = page.Height.Point;

            using var gfx = XGraphics.FromPdfPage(page);

            // Apply placeholder overlays
            var pagePositions = placeholderPositions
                .Where(p => p.PageIndex == pageIdx)
                .ToList();

            foreach (var pos in pagePositions)
            {
                if (!fieldValues.TryGetValue(pos.PlaceholderName, out var replacementValue))
                    continue;

                // Draw white rectangle over placeholder (PdfSharp uses top-left origin)
                double sharpY = pageHeight - pos.Top; // Convert from bottom-left to top-left
                var coverRect = new XRect(pos.Left - 1, sharpY - 1,
                    pos.Width + 2, pos.Height + 2);
                gfx.DrawRectangle(XBrushes.White, coverRect);

                // Draw replacement text at the same position
                var font = new XFont("Arial", pos.FontSize > 0 ? pos.FontSize : 10);
                gfx.DrawString(replacementValue, font, XBrushes.Black,
                    new XPoint(pos.Left, sharpY + pos.Height - 1));
            }

            // Apply annotations for this page
            if (annotations != null)
            {
                foreach (var annotation in annotations.Where(a => a.PageIndex == pageIdx && !string.IsNullOrWhiteSpace(a.Text)))
                {
                    double annX = annotation.NormX * pageWidth;
                    double annY = annotation.NormY * pageHeight;
                    var font = new XFont("Arial", annotation.FontSize > 0 ? annotation.FontSize : 12);
                    gfx.DrawString(annotation.Text, font, XBrushes.DarkBlue,
                        new XPoint(annX, annY));
                }
            }
        }

        using var ms = new MemoryStream();
        pdfDoc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>
    /// Uses PdfPig to find the positions of {{placeholder}} tokens in the PDF.
    /// Returns approximate bounding boxes for each placeholder occurrence.
    /// </summary>
    private List<PlaceholderPosition> FindPlaceholderPositions(string templatePath)
    {
        var positions = new List<PlaceholderPosition>();

        using var pdfDoc = UglyToad.PdfPig.PdfDocument.Open(templatePath);

        foreach (var page in pdfDoc.GetPages())
        {
            var words = page.GetWords().ToList();
            var pageText = string.Join(" ", words.Select(w => w.Text));

            // Find placeholder patterns and map back to word positions
            foreach (Match match in PlaceholderRegex.Matches(page.Text))
            {
                var placeholderText = match.Value; // e.g. "{{Name}}"
                var placeholderName = match.Groups[1].Value; // e.g. "Name"

                // Find words that contain parts of this placeholder
                var matchingWords = FindWordsForPlaceholder(words, placeholderText);

                if (matchingWords.Count > 0)
                {
                    // Compute bounding box encompassing all matching words
                    double left = matchingWords.Min(w => w.BoundingBox.Left);
                    double bottom = matchingWords.Min(w => w.BoundingBox.Bottom);
                    double right = matchingWords.Max(w => w.BoundingBox.Right);
                    double top = matchingWords.Max(w => w.BoundingBox.Top);

                    // Estimate font size from word height
                    double avgHeight = matchingWords.Average(w => w.BoundingBox.Height);

                    positions.Add(new PlaceholderPosition
                    {
                        PlaceholderName = placeholderName,
                        PageIndex = page.Number - 1,
                        Left = left,
                        Top = top,
                        Width = right - left,
                        Height = top - bottom,
                        FontSize = avgHeight
                    });
                }
            }
        }

        return positions;
    }

    private static List<Word> FindWordsForPlaceholder(List<Word> words, string placeholderText)
    {
        var result = new List<Word>();

        // Try to find the placeholder as a single word first
        foreach (var word in words)
        {
            if (word.Text.Contains(placeholderText, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(word);
                return result;
            }
        }

        // Try to find placeholder split across adjacent words (e.g. "{{" "Name" "}}")
        for (int i = 0; i < words.Count; i++)
        {
            if (!words[i].Text.Contains("{{")) continue;

            var combined = words[i].Text;
            var group = new List<Word> { words[i] };

            for (int j = i + 1; j < words.Count && j < i + 5; j++)
            {
                combined += words[j].Text;
                group.Add(words[j]);

                if (combined.Contains(placeholderText, StringComparison.OrdinalIgnoreCase))
                    return group;

                if (combined.Contains("}}"))
                    break;
            }
        }

        return result;
    }

    private static byte[] ConvertBgraToSkiaPng(byte[] bgraData, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        System.Runtime.InteropServices.Marshal.Copy(bgraData, 0, bitmap.GetPixels(), bgraData.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
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

    private class PlaceholderPosition
    {
        public string PlaceholderName { get; set; } = "";
        public int PageIndex { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double FontSize { get; set; }
    }
}
