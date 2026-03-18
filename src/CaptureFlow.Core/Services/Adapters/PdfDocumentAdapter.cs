using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Docnet.Core;
using Docnet.Core.Models;

namespace CaptureFlow.Core.Services.Adapters;

public class PdfDocumentAdapter : IDocumentAdapter
{
    private readonly ILogger<PdfDocumentAdapter> _logger;

    public PdfDocumentAdapter(ILogger<PdfDocumentAdapter> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<SupportedFileType> SupportedTypes => [SupportedFileType.Pdf];

    public bool CanHandle(string filePath)
        => Path.GetExtension(filePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<SourceDocument> LoadAsync(string filePath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var fileInfo = new FileInfo(filePath);
            var doc = new SourceDocument
            {
                FilePath = filePath,
                FileName = fileInfo.Name,
                FileType = SupportedFileType.Pdf,
                FileSizeBytes = fileInfo.Length,
                State = ProcessingState.Loading
            };

            try
            {
                using var pdfDoc = PdfDocument.Open(filePath);

                doc.Metadata.Title = GetMetadataValue(pdfDoc, "Title");
                doc.Metadata.Author = GetMetadataValue(pdfDoc, "Author");
                doc.Metadata.Subject = GetMetadataValue(pdfDoc, "Subject");

                foreach (var pdfPage in pdfDoc.GetPages())
                {
                    ct.ThrowIfCancellationRequested();

                    var page = new DocumentPage
                    {
                        PageIndex = pdfPage.Number - 1,
                        OriginalWidth = pdfPage.Width,
                        OriginalHeight = pdfPage.Height,
                    };

                    var textFragments = new List<TextFragment>();
                    foreach (var word in pdfPage.GetWords())
                    {
                        var bounds = word.BoundingBox;
                        var normRect = new NormalisedRect(
                            bounds.Left / pdfPage.Width,
                            1.0 - (bounds.Top / pdfPage.Height),
                            (bounds.Right - bounds.Left) / pdfPage.Width,
                            (bounds.Top - bounds.Bottom) / pdfPage.Height
                        );

                        textFragments.Add(new TextFragment
                        {
                            Text = word.Text,
                            Bounds = normRect.Clamp(),
                            Source = TextSource.Native,
                            PageIndex = page.PageIndex
                        });
                    }

                    page.NativeTextFragments.AddRange(textFragments);
                    page.PlainText = pdfPage.Text;
                    doc.Pages.Add(page);
                }

                doc.State = ProcessingState.Loaded;
                _logger.LogInformation("Loaded PDF {FileName} with {PageCount} pages", doc.FileName, doc.PageCount);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                doc.State = ProcessingState.Error;
                doc.ErrorMessage = $"Failed to load PDF: {ex.Message}";
                _logger.LogError(ex, "Failed to load PDF {FilePath}", filePath);
            }

            return doc;
        }, ct);
    }

    public async Task<byte[]> RenderPageAsync(SourceDocument document, int pageIndex, int widthPx, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var library = DocLib.Instance;
            using var docReader = library.GetDocReader(document.FilePath, new PageDimensions(widthPx, widthPx * 2));
            using var pageReader = docReader.GetPageReader(pageIndex);

            var rawBytes = pageReader.GetImage();
            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();

            if (rawBytes == null || rawBytes.Length == 0)
                return [];

            return ConvertBgraToSkiaPng(rawBytes, width, height);
        }, ct);
    }

    private static byte[] ConvertBgraToSkiaPng(byte[] bgraData, int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        System.Runtime.InteropServices.Marshal.Copy(bgraData, 0, bitmap.GetPixels(), bgraData.Length);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static string? GetMetadataValue(PdfDocument doc, string key)
    {
        try
        {
            var info = doc.Information;
            return key switch
            {
                "Title" => info.Title,
                "Author" => info.Author,
                "Subject" => info.Subject,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
