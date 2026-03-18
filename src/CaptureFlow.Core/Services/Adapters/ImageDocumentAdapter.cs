using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CaptureFlow.Core.Services.Adapters;

public sealed class ImageDocumentAdapter : IDocumentAdapter
{
    private readonly ILogger<ImageDocumentAdapter> _logger;

    public ImageDocumentAdapter(ILogger<ImageDocumentAdapter> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<SupportedFileType> SupportedTypes { get; } =
        new[] { SupportedFileType.Png, SupportedFileType.Jpg, SupportedFileType.Tiff, SupportedFileType.Bmp };

    public bool CanHandle(string filePath)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".tiff" or ".tif" or ".bmp";
    }

    public Task<SourceDocument> LoadAsync(string filePath, CancellationToken ct = default)
    {
        _logger.LogInformation("Loading image document: {FilePath}", filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Image file not found.", filePath);

        var fileInfo = new FileInfo(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        var fileType = ext switch
        {
            ".png" => SupportedFileType.Png,
            ".jpg" or ".jpeg" => SupportedFileType.Jpg,
            ".tiff" or ".tif" => SupportedFileType.Tiff,
            ".bmp" => SupportedFileType.Bmp,
            _ => SupportedFileType.Png
        };

        // For multi-frame formats like TIFF, load each frame as a separate page
        var pages = new List<DocumentPage>();

        if (ext is ".tiff" or ".tif")
        {
            pages = LoadTiffPages(filePath, ct);
        }
        else
        {
            using var stream = File.OpenRead(filePath);
            using var codec = SKCodec.Create(stream);

            if (codec == null)
                throw new InvalidOperationException($"Unable to decode image: {filePath}");

            var info = codec.Info;

            var page = new DocumentPage
            {
                PageIndex = 0,
                OriginalWidth = info.Width,
                OriginalHeight = info.Height,
                PlainText = null, // No native text - OCR path only
                NativeTextFragments = new List<TextFragment>(),
                OcrTextFragments = new List<TextFragment>()
            };

            pages.Add(page);
        }

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

        _logger.LogInformation("Loaded image with {PageCount} page(s). Dimensions obtained; text requires OCR.", pages.Count);
        return Task.FromResult(doc);
    }

    public Task<byte[]> RenderPageAsync(SourceDocument document, int pageIndex, int widthPx, CancellationToken ct = default)
    {
        if (pageIndex < 0 || pageIndex >= document.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        var page = document.Pages[pageIndex];
        var ext = Path.GetExtension(document.FilePath).ToLowerInvariant();

        SKBitmap bitmap;

        if (ext is ".tiff" or ".tif")
        {
            bitmap = LoadTiffFrame(document.FilePath, pageIndex);
        }
        else
        {
            bitmap = SKBitmap.Decode(document.FilePath);
        }

        if (bitmap == null)
            throw new InvalidOperationException($"Failed to decode image page {pageIndex} from {document.FilePath}");

        using (bitmap)
        {
            ct.ThrowIfCancellationRequested();

            float scale = widthPx / (float)bitmap.Width;
            int heightPx = (int)(bitmap.Height * scale);

            using var resized = bitmap.Resize(new SKImageInfo(widthPx, heightPx), SKFilterQuality.High);
            if (resized == null)
                throw new InvalidOperationException("Failed to resize image.");

            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            return Task.FromResult(data.ToArray());
        }
    }

    private static List<DocumentPage> LoadTiffPages(string filePath, CancellationToken ct)
    {
        var pages = new List<DocumentPage>();

        using var stream = File.OpenRead(filePath);
        using var codec = SKCodec.Create(stream);

        if (codec == null)
            throw new InvalidOperationException($"Unable to decode TIFF: {filePath}");

        int frameCount = codec.FrameCount;

        // If FrameCount is 0, treat as single-page image
        if (frameCount == 0)
        {
            var info = codec.Info;
            pages.Add(new DocumentPage
            {
                PageIndex = 0,
                OriginalWidth = info.Width,
                OriginalHeight = info.Height,
                PlainText = null,
                NativeTextFragments = new List<TextFragment>(),
                OcrTextFragments = new List<TextFragment>()
            });
            return pages;
        }

        for (int i = 0; i < frameCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            var frameInfo = codec.FrameInfo[i];
            var info = codec.Info;

            pages.Add(new DocumentPage
            {
                PageIndex = i,
                OriginalWidth = info.Width,
                OriginalHeight = info.Height,
                PlainText = null,
                NativeTextFragments = new List<TextFragment>(),
                OcrTextFragments = new List<TextFragment>()
            });
        }

        return pages;
    }

    private static SKBitmap LoadTiffFrame(string filePath, int frameIndex)
    {
        using var stream = File.OpenRead(filePath);
        using var codec = SKCodec.Create(stream);

        if (codec == null)
            throw new InvalidOperationException($"Unable to decode TIFF: {filePath}");

        var imageInfo = new SKImageInfo(codec.Info.Width, codec.Info.Height);
        var bitmap = new SKBitmap(imageInfo);

        if (codec.FrameCount == 0 && frameIndex == 0)
        {
            codec.GetPixels(imageInfo, bitmap.GetPixels());
            return bitmap;
        }

        var options = new SKCodecOptions(frameIndex);
        codec.GetPixels(imageInfo, bitmap.GetPixels(), options);
        return bitmap;
    }
}
