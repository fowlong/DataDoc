using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CaptureFlow.Core.Services.Adapters;

public sealed class PlainTextAdapter : IDocumentAdapter
{
    private const int LinesPerPage = 60;
    private const int PageWidthPx = 612;
    private const int PageHeightPx = 792;
    private const float Margin = 40f;
    private const float LineHeight = 14f;
    private const float FontSize = 11f;
    private const int WrapChars = 80;

    private readonly ILogger<PlainTextAdapter> _logger;

    public PlainTextAdapter(ILogger<PlainTextAdapter> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<SupportedFileType> SupportedTypes { get; } =
        new[] { SupportedFileType.Txt, SupportedFileType.Rtf, SupportedFileType.Html };

    public bool CanHandle(string filePath)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        return ext is ".txt" or ".rtf" or ".html" or ".htm";
    }

    public async Task<SourceDocument> LoadAsync(string filePath, CancellationToken ct = default)
    {
        _logger.LogInformation("Loading text-based document: {FilePath}", filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found.", filePath);

        var fileInfo = new FileInfo(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        var rawContent = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct);
        var plainText = ext switch
        {
            ".html" or ".htm" => StripHtml(rawContent),
            ".rtf" => StripRtf(rawContent),
            _ => rawContent
        };

        var fileType = ext switch
        {
            ".txt" => SupportedFileType.Txt,
            ".rtf" => SupportedFileType.Rtf,
            ".html" or ".htm" => SupportedFileType.Html,
            _ => SupportedFileType.Txt
        };

        var allLines = SplitAndWrap(plainText, WrapChars);
        var pages = BuildPages(allLines);

        var doc = new SourceDocument
        {
            Id = Guid.NewGuid().ToString(),
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            FileType = fileType,
            Pages = pages,
            Metadata = new DocumentMetadata(),
            State = ProcessingState.Loaded,
            FileSizeBytes = fileInfo.Length
        };

        _logger.LogInformation("Loaded text document with {PageCount} synthetic pages.", pages.Count);
        return doc;
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
            Typeface = SKTypeface.FromFamilyName("Courier New", SKFontStyle.Normal)
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

    private static string StripHtml(string html)
    {
        // Remove script and style blocks entirely
        var cleaned = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", string.Empty, RegexOptions.IgnoreCase);

        // Replace <br>, <p>, <div>, <li> with newlines for readability
        cleaned = Regex.Replace(cleaned, @"<(br|p|div|li|tr|h[1-6])[^>]*?>", Environment.NewLine, RegexOptions.IgnoreCase);

        // Strip remaining tags
        cleaned = Regex.Replace(cleaned, @"<[^>]+>", string.Empty);

        // Decode common HTML entities
        cleaned = System.Net.WebUtility.HtmlDecode(cleaned);

        // Collapse multiple blank lines
        cleaned = Regex.Replace(cleaned, @"(\r?\n){3,}", Environment.NewLine + Environment.NewLine);

        return cleaned.Trim();
    }

    private static string StripRtf(string rtf)
    {
        if (!rtf.TrimStart().StartsWith("{\\rtf", StringComparison.Ordinal))
            return rtf;

        // Remove RTF header/control words and extract plain text
        var sb = new StringBuilder();
        int braceDepth = 0;
        bool skipGroup = false;
        int i = 0;

        while (i < rtf.Length)
        {
            char c = rtf[i];

            if (c == '{')
            {
                braceDepth++;
                // Skip known non-text groups
                if (i + 1 < rtf.Length && rtf[i + 1] == '\\')
                {
                    var ahead = rtf.Substring(i, Math.Min(30, rtf.Length - i));
                    if (Regex.IsMatch(ahead, @"^\{\\(fonttbl|colortbl|stylesheet|pict|header|footer|footnote|info)\b"))
                    {
                        skipGroup = true;
                    }
                }
                i++;
                continue;
            }

            if (c == '}')
            {
                braceDepth--;
                if (braceDepth <= 1)
                    skipGroup = false;
                i++;
                continue;
            }

            if (skipGroup)
            {
                i++;
                continue;
            }

            if (c == '\\')
            {
                i++;
                if (i >= rtf.Length) break;

                // Escaped characters
                if (rtf[i] == '\\' || rtf[i] == '{' || rtf[i] == '}')
                {
                    sb.Append(rtf[i]);
                    i++;
                    continue;
                }

                // Read control word
                int start = i;
                while (i < rtf.Length && char.IsLetter(rtf[i]))
                    i++;

                var word = rtf[start..i];

                // Skip optional numeric parameter
                while (i < rtf.Length && (char.IsDigit(rtf[i]) || rtf[i] == '-'))
                    i++;

                // Skip single trailing space after control word
                if (i < rtf.Length && rtf[i] == ' ')
                    i++;

                switch (word)
                {
                    case "par":
                    case "line":
                        sb.AppendLine();
                        break;
                    case "tab":
                        sb.Append('\t');
                        break;
                }

                continue;
            }

            // Skip carriage returns and line feeds in RTF source (they are not meaningful)
            if (c == '\r' || c == '\n')
            {
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString().Trim();
    }

    private static List<string> SplitAndWrap(string text, int maxChars)
    {
        var result = new List<string>();
        var rawLines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        foreach (var rawLine in rawLines)
        {
            if (rawLine.Length <= maxChars)
            {
                result.Add(rawLine);
            }
            else
            {
                int pos = 0;
                while (pos < rawLine.Length)
                {
                    int len = Math.Min(maxChars, rawLine.Length - pos);
                    if (pos + len < rawLine.Length)
                    {
                        int breakAt = rawLine.LastIndexOf(' ', pos + len - 1, len);
                        if (breakAt > pos)
                            len = breakAt - pos;
                    }

                    result.Add(rawLine.Substring(pos, len).TrimEnd());
                    pos += len;
                    if (pos < rawLine.Length && rawLine[pos] == ' ')
                        pos++;
                }
            }
        }

        return result;
    }

    private static List<DocumentPage> BuildPages(List<string> allLines)
    {
        var pages = new List<DocumentPage>();
        int totalLines = allLines.Count;
        int pageCount = Math.Max(1, (int)Math.Ceiling(totalLines / (double)LinesPerPage));

        for (int p = 0; p < pageCount; p++)
        {
            int startLine = p * LinesPerPage;
            int count = Math.Min(LinesPerPage, totalLines - startLine);
            var pageLines = count > 0 ? allLines.GetRange(startLine, count) : new List<string>();
            var plainText = string.Join(Environment.NewLine, pageLines);

            var fragments = new List<TextFragment>();
            float usableWidth = PageWidthPx - 2 * Margin;

            for (int i = 0; i < pageLines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(pageLines[i]))
                    continue;

                float normY = (Margin + i * LineHeight) / PageHeightPx;
                float normHeight = LineHeight / PageHeightPx;
                float normX = Margin / PageWidthPx;
                float normWidth = usableWidth / PageWidthPx;

                fragments.Add(new TextFragment
                {
                    Text = pageLines[i],
                    Bounds = new NormalisedRect(
                        Math.Clamp(normX, 0, 1),
                        Math.Clamp(normY, 0, 1),
                        Math.Clamp(normWidth, 0, 1),
                        Math.Clamp(normHeight, 0, 1)),
                    Source = TextSource.Native,
                    Confidence = 1.0f,
                    PageIndex = p
                });
            }

            pages.Add(new DocumentPage
            {
                PageIndex = p,
                OriginalWidth = PageWidthPx,
                OriginalHeight = PageHeightPx,
                PlainText = plainText,
                NativeTextFragments = fragments
            });
        }

        return pages;
    }
}
