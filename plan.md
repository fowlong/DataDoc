# Plan: PDF Text & Image Extraction for Base Document

## Goal
When a user loads a base PDF (or DOCX-converted-to-PDF), extract all text and images with exact positioning/styling, overlay them as editable pdfme schema elements, and import the "stripped" PDF (no text/images) as the background.

## Architecture Overview

```
User loads PDF
    │
    ├─► [C#] PdfContentExtractor (new service)
    │   ├─ Extract text runs (grouped letters → runs with font, size, color, position)
    │   ├─ Extract embedded images (with position, dimensions, raw bytes)
    │   ├─ Extract font metadata (name, embedded/subset flag)
    │   └─ Generate stripped PDF (redact text & images, keep vector/backgrounds)
    │
    ├─► [C#] FontMatcher (new service)
    │   ├─ Map PDF font names → known font families
    │   ├─ Detect subset fonts (prefix like ABCDEF+FontName)
    │   └─ Match to closest Google Font (cached mapping table)
    │
    ├─► [C# → JS] Pass to pdfme designer via bridge
    │   ├─ Stripped PDF as basePdf
    │   ├─ Text runs → text schemas (with font, size, color, position)
    │   ├─ Images → image schemas (base64 data, position, dimensions)
    │   └─ Font files → pdfme font registry
    │
    └─► [JS] pdfme designer renders everything as editable overlays
```

## Phase 1: Text Extraction with Full Metadata

### Library: PdfPig (already in project)
PdfPig's `page.Letters` API provides per-character:
- `Letter.Value` — the character
- `Letter.Location` — exact x, y position
- `Letter.FontName` — full font name (e.g. "ABCDEF+Arial-Bold")
- `Letter.PointSize` — font size in points
- `Letter.Color` — RGB color (if available)
- `Letter.GlyphRectangle` — precise bounding box
- `Letter.Font.IsBold`, `Letter.Font.IsItalic` — style flags

### New Service: `PdfContentExtractor`
**File:** `src/CaptureFlow.Core/Services/PdfContentExtractor.cs`

```csharp
public class PdfContentExtractor
{
    public PdfContentExtractionResult Extract(byte[] pdfBytes);
}

public class PdfContentExtractionResult
{
    public List<ExtractedTextRun> TextRuns { get; set; }
    public List<ExtractedImage> Images { get; set; }
    public List<ExtractedFont> Fonts { get; set; }
    public byte[] StrippedPdfBytes { get; set; }  // PDF with text/images removed
}

public class ExtractedTextRun
{
    public int PageIndex { get; set; }
    public string Text { get; set; }
    public double X { get; set; }          // mm from left
    public double Y { get; set; }          // mm from top
    public double Width { get; set; }      // mm
    public double Height { get; set; }     // mm
    public string FontName { get; set; }   // cleaned font name
    public double FontSize { get; set; }   // pt
    public string FontColor { get; set; }  // hex #RRGGBB
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
}

public class ExtractedImage
{
    public int PageIndex { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public byte[] ImageBytes { get; set; }  // PNG
    public string MimeType { get; set; }
}

public class ExtractedFont
{
    public string OriginalName { get; set; }  // e.g. "ABCDEF+Arial-Bold"
    public string CleanName { get; set; }     // e.g. "Arial"
    public bool IsSubset { get; set; }        // has ABCDEF+ prefix
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public string? MatchedGoogleFont { get; set; }  // e.g. "Roboto"
    public byte[]? EmbeddedFontData { get; set; }   // if fully embedded
}
```

### Text Run Grouping Algorithm — Line-level with Spacing Preservation
The goal: one schema per **line of text** (not per word), preserving exact inter-word and inter-character spacing so the output is visually identical.

**Step 1: Collect letters per page**
- `page.Letters` gives every character with position, font, size, color

**Step 2: Group into lines**
- Sort letters by Y position (bottom-to-top in PDF coords)
- Cluster letters whose Y-center falls within a tolerance (e.g. `fontSize * 0.3`) — these are on the same line
- Within each line cluster, sort by X position (left-to-right)

**Step 3: Split lines by style changes**
- Within a line, if font/size/color changes between consecutive letters, split into separate runs at that boundary
- This handles mixed-style lines (e.g. a bold word in a regular sentence) as separate schemas positioned side-by-side

**Step 4: Preserve spacing**
- For each run, calculate the text content by examining gaps between consecutive letters:
  - If the gap between `letter[i].EndX` and `letter[i+1].StartX` exceeds a threshold (`fontSize * 0.15`), insert a space character
  - If the gap is larger than `fontSize * 0.5`, insert multiple spaces or treat as separate runs (tab stops / column alignment)
- Compute `characterSpacing` for the pdfme schema:
  - `characterSpacing = (totalRunWidth - sumOfGlyphWidths) / (numChars - 1)` — this distributes any extra spacing evenly
  - This approximation works well for justified text and most typeset content
- Store the exact bounding box width so the schema width matches the original line extent

**Step 5: Build ExtractedTextRun**
```csharp
public class ExtractedTextRun
{
    public int PageIndex { get; set; }
    public string Text { get; set; }           // full line text with spaces
    public double X { get; set; }              // mm, left edge
    public double Y { get; set; }              // mm, top edge (pdfme coords)
    public double Width { get; set; }          // mm, exact line width
    public double Height { get; set; }         // mm, line height
    public string FontName { get; set; }       // cleaned font name
    public double FontSize { get; set; }       // pt
    public string FontColor { get; set; }      // hex #RRGGBB
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public double CharacterSpacing { get; set; } // pt, for pdfme schema
    public double LineHeight { get; set; }      // ratio, for multi-line blocks
}
```

**Step 6 (optional optimisation): Merge consecutive same-style lines into multi-line blocks**
- If two consecutive lines share the same font/size/color/X-position (same paragraph alignment):
  - Merge into a single schema with newlines in content
  - Set `lineHeight` to match the actual inter-line spacing: `lineSpacing_mm / fontSize_mm`
  - This reduces schema count significantly for body text paragraphs
- Only merge if the line spacing is consistent (within 5% tolerance)

### Coordinate Conversion
- PdfPig uses PDF coordinates: origin at bottom-left, Y increases upward, units in points (1pt = 1/72 inch)
- pdfme uses: origin at top-left, Y increases downward, units in mm
- Conversion: `y_pdfme = (pageHeight_pt - y_pdf_top) * 25.4 / 72`
- `x_pdfme = x_pdf * 25.4 / 72`

## Phase 2: Image Extraction

### Library: PdfPig (already in project)
PdfPig can extract images via `page.GetImages()`:
- `IPdfImage.Bounds` — position and dimensions
- `IPdfImage.RawBytes` — raw image data
- `IPdfImage.TryGetPng()` — attempt PNG conversion

### Process
1. For each page, call `page.GetImages()`
2. Convert each image to PNG (PdfPig handles JPEG, CCITT, etc.)
3. Record position in pdfme coordinates (mm, top-left origin)
4. Store as base64 for pdfme image schema

## Phase 3: Stripped PDF Generation

### Approach: Render-to-image fallback
The most reliable approach for "removing" text and images from a PDF:

**Option A (Preferred — Rasterize + reconstruct):**
1. Use Docnet (already in project) to render each page as a high-resolution image (300 DPI)
2. Use PdfPig to identify text/image bounding boxes
3. Use SkiaSharp to paint white over those regions in the rendered image
4. Reconstruct a PDF from the cleaned images using PdfSharpCore
5. This becomes the basePdf for pdfme

**Pros:** Reliable, works for any PDF, no need to parse content streams
**Cons:** Loses vector sharpness, larger file size

**Option B (Content stream manipulation):**
1. Parse PDF content streams and remove text-drawing (Tj, TJ, etc.) and image-drawing (Do) operators
2. Keep vector graphics (path operators, fills, strokes)

**Pros:** Retains vector quality, smaller file
**Cons:** Very complex, fragile, many edge cases (Form XObjects, Type3 fonts, patterns)

**Recommendation:** Start with Option A for reliability. We can explore Option B later as an optimization for PDFs with simple structure.

### Implementation
```csharp
public byte[] GenerateStrippedPdf(byte[] originalPdfBytes, PdfContentExtractionResult extraction)
{
    // For each page:
    // 1. Render at 300 DPI using Docnet
    // 2. Create SKBitmap from rendered data
    // 3. Paint white rectangles over all text/image bounding boxes (with small padding)
    // 4. Create new PDF page with cleaned image using PdfSharpCore
    // Return new PDF bytes
}
```

## Phase 4: Font Matching

### Subset Font Detection
PDF fonts with names like `ABCDEF+Arial-Bold` are subsets — only contain glyphs used in the document. These can't be re-used for arbitrary text.

### Strategy
1. **Fully embedded fonts:** Extract the font program data from PdfPig, convert to TTF/OTF if needed, register with pdfme
2. **Subset fonts:** Strip the `ABCDEF+` prefix, look up the base font name
3. **Standard 14 fonts:** Direct mapping (Helvetica→Arial, Times→Times New Roman, Courier→Courier New)
4. **Google Fonts matching:** Maintain a static mapping table of ~200 common font names → Google Font equivalents

### Font Mapping Table (static, built-in)
```csharp
private static readonly Dictionary<string, string> FontToGoogleFont = new()
{
    // Standard PDF fonts
    ["Helvetica"] = "Open Sans",
    ["Helvetica-Bold"] = "Open Sans",
    ["Times-Roman"] = "Noto Serif",
    ["Times-Bold"] = "Noto Serif",
    ["Courier"] = "Roboto Mono",

    // Common Windows fonts
    ["Arial"] = "Open Sans",
    ["Arial-Bold"] = "Open Sans",
    ["TimesNewRoman"] = "Noto Serif",
    ["Calibri"] = "Carlito",
    ["Cambria"] = "Caladea",
    ["Verdana"] = "Open Sans",
    ["Georgia"] = "Noto Serif",
    ["Tahoma"] = "Open Sans",
    ["Segoe UI"] = "Open Sans",
    ["Trebuchet MS"] = "Fira Sans",
    ["Palatino"] = "Noto Serif",
    ["Garamond"] = "EB Garamond",
    ["Century Gothic"] = "Poppins",
    ["Futura"] = "Nunito Sans",
    ["Gill Sans"] = "Lato",
    ["Rockwell"] = "Rokkitt",
    ["Lucida Console"] = "Roboto Mono",
    ["Consolas"] = "Roboto Mono",
    // ... ~200 more mappings
};
```

### Google Font Download & Cache
1. On first use of a Google Font, download TTF from `https://fonts.google.com/download?family=FontName`
2. Cache in `%APPDATA%/CaptureFlow/Fonts/`
3. Register with pdfme via the font configuration system

## Phase 5: pdfme Integration

### Font Registration in pdfme
pdfme supports custom fonts via the `font` option in Designer:
```javascript
var font = {
    'Open Sans': {
        data: base64FontData,   // base64-encoded TTF
        fallback: true
    },
    'Roboto Mono': {
        data: base64FontData
    }
};

new Designer({ domContainer, template, options: { font } });
```

### New JS API: `loadExtractedContent`
```javascript
loadExtractedContent: function(contentJson) {
    var content = JSON.parse(contentJson);
    var template = designer.getTemplate();

    // Update basePdf to stripped version
    template.basePdf = content.strippedPdfBase64;

    // Add text schemas for each page
    content.textRuns.forEach(function(run) {
        var pageIdx = run.pageIndex;
        while (template.schemas.length <= pageIdx) template.schemas.push([]);
        template.schemas[pageIdx].push({
            name: 'text_' + pageIdx + '_' + template.schemas[pageIdx].length,
            type: 'text',
            content: run.text,
            position: { x: run.x, y: run.y },
            width: run.width,
            height: run.height,
            fontSize: run.fontSize,
            fontName: run.fontName,
            fontColor: run.fontColor,
            alignment: 'left',
            // ... other defaults
        });
    });

    // Add image schemas
    content.images.forEach(function(img) {
        var pageIdx = img.pageIndex;
        template.schemas[pageIdx].push({
            name: 'img_' + pageIdx + '_' + template.schemas[pageIdx].length,
            type: 'image',
            content: img.base64Data,
            position: { x: img.x, y: img.y },
            width: img.width,
            height: img.height
        });
    });

    initDesigner(template);
}
```

### New Bridge Method
```csharp
// IDesignerBridge
Task LoadExtractedContentAsync(string contentJson);
```

### UI Flow
1. User clicks "Load Base Document..."
2. System loads PDF → shows loading spinner
3. `PdfContentExtractor.Extract()` runs in background
4. `FontMatcher` resolves fonts, downloads Google Fonts if needed
5. Stripped PDF + schemas sent to pdfme via bridge
6. Designer shows the document with all text/images as editable overlays
7. User sees exact replica — but every text block and image is selectable/editable

## Phase 6: CreateViewModel Changes

### New Properties
```csharp
[ObservableProperty] private bool _isExtracting;
[ObservableProperty] private bool _extractTextOnLoad = true;  // toggle in UI
```

### Modified LoadBaseDocument Flow
```csharp
private async Task LoadBaseDocument()
{
    // ... existing file dialog ...

    if (_extractTextOnLoad)
    {
        IsExtracting = true;
        StatusText = "Extracting text and images...";

        var result = await Task.Run(() => _contentExtractor.Extract(pdfBytes));

        StatusText = $"Found {result.TextRuns.Count} text blocks, {result.Images.Count} images";

        // Resolve fonts
        var fontData = await _fontMatcher.ResolveFontsAsync(result.Fonts);

        // Register fonts with pdfme
        await _designerBridge.RegisterFontsAsync(fontData);

        // Load stripped PDF + overlays
        var contentJson = BuildContentJson(result);
        await _designerBridge.LoadExtractedContentAsync(contentJson);

        IsExtracting = false;
    }
    else
    {
        // Current behavior — just load PDF as background
        await _designerBridge.LoadBasePdfAsync(base64);
    }
}
```

## File Summary

### New Files
1. `src/CaptureFlow.Core/Services/PdfContentExtractor.cs` — Main extraction service
2. `src/CaptureFlow.Core/Services/FontMatcher.cs` — Font name resolution + Google Font download
3. `src/CaptureFlow.Core/Models/PdfContentModels.cs` — Data models for extraction results

### Modified Files
4. `src/CaptureFlow.App/ViewModels/CreateViewModel.cs` — Updated LoadBaseDocument flow
5. `src/CaptureFlow.App/Views/CreatePanel.xaml.cs` — New bridge methods
6. `src/CaptureFlow.App/Resources/pdfme-designer.html` — New JS APIs (loadExtractedContent, registerFonts)
7. `src/CaptureFlow.App/Views/CreatePanel.xaml` — Toggle for text extraction on load
8. `src/CaptureFlow.App/App.xaml.cs` — DI registration

### No New NuGet Packages Required
- PdfPig (existing) — text/image extraction with full metadata
- PdfSharpCore (existing) — stripped PDF construction
- Docnet.Core (existing) — page rendering for strip approach
- SkiaSharp (existing) — image manipulation for stripping

## Implementation Order
1. `PdfContentModels.cs` — Data models
2. `PdfContentExtractor.cs` — Text + image extraction (core logic)
3. `PdfContentExtractor.cs` — Stripped PDF generation
4. `FontMatcher.cs` — Font name mapping + Google Font download
5. `pdfme-designer.html` — JS APIs for content loading + font registration
6. `CreatePanel.xaml.cs` — Bridge methods
7. `CreateViewModel.cs` — Updated load flow with extraction toggle
8. `CreatePanel.xaml` — UI toggle
9. Integration testing + coordinate calibration

## Risks & Mitigations
- **Text grouping accuracy:** Letters may not group correctly for complex layouts (tables, columns). Mitigation: use tolerance-based grouping, offer manual adjustment in designer.
- **Font matching quality:** Some fonts won't have close Google Font matches. Mitigation: fall back to generic families (serif, sans-serif, monospace).
- **Stripped PDF quality:** Raster approach loses vector sharpness. Mitigation: use 300 DPI, offer toggle to skip stripping.
- **Large PDFs:** Many text runs = many schemas = potential performance issue. Mitigation: limit to first N pages, merge adjacent same-style runs aggressively.
- **Right-to-left / CJK text:** PdfPig handles these but grouping logic needs to account for reading direction.
