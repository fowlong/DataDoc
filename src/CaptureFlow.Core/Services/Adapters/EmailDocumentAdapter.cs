using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using Microsoft.Extensions.Logging;
using MimeKit;
using MsgReader.Outlook;
using SkiaSharp;

namespace CaptureFlow.Core.Services.Adapters;

public sealed class EmailDocumentAdapter : IDocumentAdapter
{
    private const int LinesPerPage = 60;
    private const int PageWidthPx = 612;
    private const int PageHeightPx = 792;
    private const float Margin = 40f;
    private const float LineHeight = 14f;
    private const float FontSize = 11f;
    private const float HeaderFontSize = 12f;
    private const int WrapChars = 80;

    private readonly ILogger<EmailDocumentAdapter> _logger;

    public EmailDocumentAdapter(ILogger<EmailDocumentAdapter> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<SupportedFileType> SupportedTypes { get; } =
        new[] { SupportedFileType.Eml, SupportedFileType.Msg };

    public bool CanHandle(string filePath)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        return ext is ".eml" or ".msg";
    }

    public async Task<SourceDocument> LoadAsync(string filePath, CancellationToken ct = default)
    {
        _logger.LogInformation("Loading email document: {FilePath}", filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Email file not found.", filePath);

        var fileInfo = new FileInfo(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        EmailContent email = ext == ".eml"
            ? await LoadEmlAsync(filePath, ct)
            : LoadMsg(filePath);

        var metadata = new DocumentMetadata
        {
            Title = email.Subject,
            Author = email.From,
            Subject = email.Subject,
            Sender = email.From,
            Recipients = email.To,
            SentDate = email.Date
        };

        // Build a formatted text representation of the email
        var sb = new StringBuilder();
        sb.AppendLine($"From: {email.From}");
        sb.AppendLine($"To: {email.To}");
        if (!string.IsNullOrWhiteSpace(email.Cc))
            sb.AppendLine($"CC: {email.Cc}");
        sb.AppendLine($"Date: {email.Date?.ToString("f") ?? "(unknown)"}");
        sb.AppendLine($"Subject: {email.Subject}");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine();
        sb.Append(email.Body);

        var fullText = sb.ToString();
        var allLines = SplitAndWrap(fullText, WrapChars);
        var pages = BuildPages(allLines);

        var fileType = ext == ".eml" ? SupportedFileType.Eml : SupportedFileType.Msg;

        var doc = new SourceDocument
        {
            Id = Guid.NewGuid().ToString(),
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            FileType = fileType,
            Pages = pages,
            Metadata = metadata,
            State = ProcessingState.Loaded,
            FileSizeBytes = fileInfo.Length
        };

        _logger.LogInformation("Loaded email with {PageCount} synthetic pages.", pages.Count);
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

        var lines = (page.PlainText ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        float y = Margin + FontSize;

        using var headerPaint = new SKPaint
        {
            Color = SKColors.DarkSlateGray,
            TextSize = HeaderFontSize,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        using var bodyPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = FontSize,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };

        // On the first page, render header lines with distinct styling
        bool inHeader = pageIndex == 0;

        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();

            if (inHeader && line.StartsWith("---"))
            {
                // Draw separator line
                canvas.DrawLine(Margin, y - FontSize / 2, PageWidthPx - Margin, y - FontSize / 2,
                    new SKPaint { Color = SKColors.Gray, StrokeWidth = 1 });
                y += LineHeight;
                inHeader = false;
                continue;
            }

            var paint = inHeader ? headerPaint : bodyPaint;
            canvas.DrawText(line, Margin, y, paint);
            y += LineHeight;

            if (y > PageHeightPx - Margin)
                break;
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return Task.FromResult(data.ToArray());
    }

    private static async Task<EmailContent> LoadEmlAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var message = await MimeMessage.LoadAsync(stream, ct);

        return new EmailContent
        {
            Subject = message.Subject ?? string.Empty,
            From = message.From?.ToString() ?? string.Empty,
            To = message.To?.ToString() ?? string.Empty,
            Cc = message.Cc?.ToString() ?? string.Empty,
            Date = message.Date != DateTimeOffset.MinValue ? message.Date.DateTime : null,
            Body = message.TextBody ?? StripHtmlBasic(message.HtmlBody ?? string.Empty)
        };
    }

    private static EmailContent LoadMsg(string filePath)
    {
        using var msg = new Storage.Message(filePath);

        return new EmailContent
        {
            Subject = msg.Subject ?? string.Empty,
            From = msg.Sender?.Email ?? msg.GetEmailSender(false, false) ?? string.Empty,
            To = msg.GetEmailRecipients(RecipientType.To, false, false) ?? string.Empty,
            Cc = msg.GetEmailRecipients(RecipientType.Cc, false, false) ?? string.Empty,
            Date = msg.SentOn,
            Body = msg.BodyText ?? StripHtmlBasic(msg.BodyHtml ?? string.Empty)
        };
    }

    private static string StripHtmlBasic(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            html, @"<(script|style)[^>]*>[\s\S]*?</\1>", string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"<(br|p|div|li|tr)[^>]*?>", Environment.NewLine,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"<[^>]+>", string.Empty);
        cleaned = System.Net.WebUtility.HtmlDecode(cleaned);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"(\r?\n){3,}",
            Environment.NewLine + Environment.NewLine);

        return cleaned.Trim();
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

    private sealed class EmailContent
    {
        public string Subject { get; init; } = string.Empty;
        public string From { get; init; } = string.Empty;
        public string To { get; init; } = string.Empty;
        public string Cc { get; init; } = string.Empty;
        public DateTime? Date { get; init; }
        public string Body { get; init; } = string.Empty;
    }
}
