using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Tesseract;

namespace CaptureFlow.Core.Services.OCR;

/// <summary>
/// OCR engine implementation backed by the Tesseract NuGet package.
/// Converts image bytes to recognised text with bounding boxes in normalised coordinates.
/// </summary>
public class TesseractOcrEngine : IOcrEngine, IDisposable
{
    private readonly ILogger<TesseractOcrEngine> _logger;
    private readonly string _tessDataPath;
    private readonly string _language;
    private TesseractEngine? _engine;
    private bool _disposed;

    /// <summary>
    /// Creates a new Tesseract OCR engine instance.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="tessDataPath">Path to the tessdata directory containing trained data files.</param>
    /// <param name="language">Tesseract language code (e.g. "eng").</param>
    public TesseractOcrEngine(
        ILogger<TesseractOcrEngine> logger,
        string tessDataPath = "./tessdata",
        string language = "eng")
    {
        _logger = logger;
        _tessDataPath = tessDataPath;
        _language = language;
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                EnsureEngine();
                return _engine != null;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<List<TextFragment>> RecognizeAsync(
        byte[] imageData,
        int pageIndex,
        double pageWidth,
        double pageHeight,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            EnsureEngine();
            if (_engine == null)
                throw new InvalidOperationException("Tesseract engine is not available.");

            var fragments = new List<TextFragment>();

            try
            {
                using var pix = Pix.LoadFromMemory(imageData);
                using var page = _engine.Process(pix);

                var imageWidth = (double)pix.Width;
                var imageHeight = (double)pix.Height;

                using var iter = page.GetIterator();
                iter.Begin();

                do
                {
                    ct.ThrowIfCancellationRequested();

                    if (!iter.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds))
                        continue;

                    var text = iter.GetText(PageIteratorLevel.Word);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    var confidence = iter.GetConfidence(PageIteratorLevel.Word) / 100.0;

                    // Convert pixel coordinates to normalised (0-1) coordinates.
                    var normRect = new NormalisedRect(
                        bounds.X1 / imageWidth,
                        bounds.Y1 / imageHeight,
                        (bounds.X2 - bounds.X1) / imageWidth,
                        (bounds.Y2 - bounds.Y1) / imageHeight
                    ).Clamp();

                    fragments.Add(new TextFragment
                    {
                        Text = text.Trim(),
                        Bounds = normRect,
                        Source = TextSource.Ocr,
                        Confidence = confidence,
                        PageIndex = pageIndex
                    });
                } while (iter.Next(PageIteratorLevel.Word));

                _logger.LogInformation(
                    "Tesseract recognised {FragmentCount} words on page {PageIndex} with mean confidence {Confidence:P1}",
                    fragments.Count, pageIndex,
                    fragments.Count > 0 ? fragments.Average(f => f.Confidence) : 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Tesseract OCR failed for page {PageIndex}", pageIndex);
                throw;
            }

            return fragments;
        }, ct);
    }

    private void EnsureEngine()
    {
        if (_engine != null)
            return;

        try
        {
            _engine = new TesseractEngine(_tessDataPath, _language, EngineMode.Default);
            _logger.LogInformation("Tesseract engine initialised with language '{Language}' from '{DataPath}'",
                _language, _tessDataPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialise Tesseract engine from '{DataPath}'", _tessDataPath);
            _engine = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine?.Dispose();
        _engine = null;
        GC.SuppressFinalize(this);
    }
}
