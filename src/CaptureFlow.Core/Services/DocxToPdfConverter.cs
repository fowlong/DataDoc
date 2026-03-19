using CaptureFlow.Core.Services.Adapters;
using Microsoft.Extensions.Logging;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace CaptureFlow.Core.Services;

/// <summary>
/// Converts DOCX files to PDF by rendering each page to PNG via DocxDocumentAdapter
/// and embedding the images into a PDF using PdfSharpCore.
/// </summary>
public sealed class DocxToPdfConverter
{
    private readonly DocumentAdapterFactory _adapterFactory;
    private readonly ILogger<DocxToPdfConverter> _logger;

    public DocxToPdfConverter(DocumentAdapterFactory adapterFactory, ILogger<DocxToPdfConverter> logger)
    {
        _adapterFactory = adapterFactory;
        _logger = logger;
    }

    public async Task<byte[]> ConvertAsync(string docxPath, CancellationToken ct = default)
    {
        if (!File.Exists(docxPath))
            throw new FileNotFoundException("DOCX file not found.", docxPath);

        var adapter = _adapterFactory.GetAdapter(docxPath)
            ?? throw new InvalidOperationException($"No adapter found for {docxPath}");

        var doc = await adapter.LoadAsync(docxPath, ct);

        _logger.LogInformation("Converting DOCX to PDF: {FileName} ({PageCount} pages)",
            doc.FileName, doc.Pages.Count);

        using var pdfDoc = new PdfDocument();
        pdfDoc.Info.Title = doc.Metadata?.Title ?? doc.FileName;

        for (int i = 0; i < doc.Pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var pngBytes = await adapter.RenderPageAsync(doc, i, 612, ct);
            if (pngBytes.Length == 0) continue;

            var page = pdfDoc.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);

            using var gfx = XGraphics.FromPdfPage(page);
            using var ms = new MemoryStream(pngBytes);
            var image = XImage.FromStream(() => new MemoryStream(pngBytes));
            gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
        }

        using var output = new MemoryStream();
        pdfDoc.Save(output, false);
        var result = output.ToArray();

        _logger.LogInformation("DOCX to PDF conversion complete: {Size} bytes", result.Length);
        return result;
    }
}
