using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CaptureFlow.Core.Services.Adapters;

public sealed class DocxDocumentAdapter : IDocumentAdapter
{
    private const int LinesPerPage = 60;
    private const int PageWidthPx = 612;
    private const int PageHeightPx = 792;
    private const float Margin = 40f;
    private const float LineHeight = 14f;
    private const float FontSize = 11f;

    private readonly ILogger<DocxDocumentAdapter> _logger;

    public DocxDocumentAdapter(ILogger<DocxDocumentAdapter> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<SupportedFileType> SupportedTypes { get; } =
        new[] { SupportedFileType.Docx };

    public bool CanHandle(string filePath)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        return ext == ".docx";
    }

    public Task<SourceDocument> LoadAsync(string filePath, CancellationToken ct = default)
    {
        _logger.LogInformation("Loading DOCX document: {FilePath}", filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("DOCX file not found.", filePath);

        var fileInfo = new FileInfo(filePath);
        var paragraphs = ExtractParagraphs(filePath);
        var metadata = ExtractMetadata(filePath);

        // Split paragraph lines into synthetic pages of ~60 lines each
        var allLines = new List<string>();
        foreach (var para in paragraphs)
        {
            if (string.IsNullOrEmpty(para))
            {
                allLines.Add(string.Empty);
            }
            else
            {
                // Wrap long paragraphs into multiple lines (~80 chars per line)
                var wrapped = WrapText(para, 80);
                allLines.AddRange(wrapped);
            }
        }

        var pages = new List<DocumentPage>();
        int totalLines = allLines.Count;
        int pageCount = Math.Max(1, (int)Math.Ceiling(totalLines / (double)LinesPerPage));

        for (int p = 0; p < pageCount; p++)
        {
            ct.ThrowIfCancellationRequested();

            int startLine = p * LinesPerPage;
            int count = Math.Min(LinesPerPage, totalLines - startLine);
            var pageLines = allLines.GetRange(startLine, count);
            var plainText = string.Join(Environment.NewLine, pageLines);

            var fragments = CreateTextFragments(pageLines, p);

            var page = new DocumentPage
            {
                PageIndex = p,
                OriginalWidth = PageWidthPx,
                OriginalHeight = PageHeightPx,
                PlainText = plainText,
                NativeTextFragments = fragments
            };

            pages.Add(page);
        }

        var doc = new SourceDocument
        {
            Id = Guid.NewGuid().ToString(),
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            FileType = SupportedFileType.Docx,
            Pages = pages,
            Metadata = metadata,
            State = ProcessingState.Loaded,
            FileSizeBytes = fileInfo.Length
        };

        _logger.LogInformation("Loaded DOCX with {PageCount} synthetic pages.", pages.Count);
        return Task.FromResult(doc);
    }

    public Task<byte[]> RenderPageAsync(SourceDocument document, int pageIndex, int widthPx, CancellationToken ct = default)
    {
        if (pageIndex < 0 || pageIndex >= document.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        var page = document.Pages[pageIndex];
        float scale = widthPx / (float)PageWidthPx;
        int heightPx = (int)(PageHeightPx * scale);

        using var surface = SKSurface.Create(new SKImageInfo(widthPx, heightPx));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);

        using var paint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = FontSize,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };

        var lines = (page.PlainText ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        float y = Margin + FontSize;

        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            canvas.DrawText(line, Margin, y, paint);
            y += LineHeight;

            if (y > PageHeightPx - Margin)
                break;
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return Task.FromResult(data.ToArray());
    }

    private static List<string> ExtractParagraphs(string filePath)
    {
        var paragraphs = new List<string>();

        using var doc = WordprocessingDocument.Open(filePath, false);
        var body = doc.MainDocumentPart?.Document?.Body;

        if (body == null)
            return paragraphs;

        foreach (var para in body.Elements<Paragraph>())
        {
            var text = para.InnerText ?? string.Empty;
            paragraphs.Add(text);
        }

        return paragraphs;
    }

    private static DocumentMetadata ExtractMetadata(string filePath)
    {
        var metadata = new DocumentMetadata();

        using var doc = WordprocessingDocument.Open(filePath, false);
        var props = doc.PackageProperties;

        if (props != null)
        {
            metadata.Title = props.Title;
            metadata.Author = props.Creator;
            metadata.Subject = props.Subject;
            metadata.CreatedDate = props.Created;
            metadata.ModifiedDate = props.Modified;
        }

        return metadata;
    }

    private static List<TextFragment> CreateTextFragments(List<string> lines, int pageIndex)
    {
        var fragments = new List<TextFragment>();
        float usableHeight = PageHeightPx - 2 * Margin;
        float usableWidth = PageWidthPx - 2 * Margin;

        for (int i = 0; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            float normY = (Margin + i * LineHeight) / PageHeightPx;
            float normHeight = LineHeight / PageHeightPx;
            float normX = Margin / PageWidthPx;
            float normWidth = usableWidth / PageWidthPx;

            fragments.Add(new TextFragment
            {
                Text = lines[i],
                Bounds = new NormalisedRect(
                    Math.Clamp(normX, 0, 1),
                    Math.Clamp(normY, 0, 1),
                    Math.Clamp(normWidth, 0, 1),
                    Math.Clamp(normHeight, 0, 1)),
                Source = TextSource.Native,
                Confidence = 1.0f,
                PageIndex = pageIndex
            });
        }

        return fragments;
    }

    private static List<string> WrapText(string text, int maxChars)
    {
        var result = new List<string>();

        if (text.Length <= maxChars)
        {
            result.Add(text);
            return result;
        }

        int pos = 0;
        while (pos < text.Length)
        {
            int len = Math.Min(maxChars, text.Length - pos);
            if (pos + len < text.Length)
            {
                int breakAt = text.LastIndexOf(' ', pos + len - 1, len);
                if (breakAt > pos)
                    len = breakAt - pos;
            }

            result.Add(text.Substring(pos, len).TrimEnd());
            pos += len;

            // Skip the space at the break point
            if (pos < text.Length && text[pos] == ' ')
                pos++;
        }

        return result;
    }
}
