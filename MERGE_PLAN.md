# Merge Tab Implementation Plan

## Problem
1. "No imaging component suitable" error — preview returns raw DOCX bytes, not renderable image
2. PDF merge is non-functional (PdfPig is read-only)
3. No additive editing capability
4. Preview doesn't actually show the merged document visually

## Phase 1: Fix DOCX Preview Rendering
**Files:** DocxMergeService.cs, MergeViewModel.cs

- DocxMergeService.GeneratePreviewAsync currently returns raw DOCX bytes
- Need to render merged DOCX → PNG using existing DocxDocumentAdapter's SkiaSharp pipeline
- Approach: after merging placeholders into DOCX bytes, load via DocxDocumentAdapter.LoadAsync (from MemoryStream temp file), then RenderPageAsync to get PNG
- Alternative (simpler): inject DocxDocumentAdapter into DocxMergeService, save merged bytes to temp file, load + render page 0, return PNG bytes
- MergeViewModel already has ByteArrayToImageConverter wired up — once we return PNG bytes, preview works

## Phase 2: Add PDF Write Support via PdfSharp
**Files:** CaptureFlow.Core.csproj, PdfMergeService.cs (rewrite)

- Add NuGet: PdfSharpCore (or PDFsharp 6.x for .NET 8)
- PdfSharp can open existing PDFs and modify text/add content
- For {{placeholder}} replacement in PDFs:
  - Read page content, find placeholder text positions
  - Draw white rectangle over placeholder, draw replacement text
  - This is "additive" editing — overlays on existing content
- For bulk generation: clone template, apply overlays per row
- GeneratePreviewAsync: render modified PDF page to image via Docnet (already available)

## Phase 3: Additive Editing Layer
**Files:** New MergeEditOverlay model, MergePanel.xaml, MergeViewModel.cs

- Model: MergeAnnotation { Text, X, Y, Width, Height, FontSize, PageIndex }
- After preview renders, user can click on the preview to place text annotations
- Annotations stored in MergeViewModel.Annotations collection
- On export: annotations applied as additional content on each generated document
- DOCX: insert text boxes at approximate positions via OpenXml
- PDF: draw text at exact positions via PdfSharp

## Phase 4: Multi-page Preview Navigation
**Files:** MergePanel.xaml, MergeViewModel.cs

- Add page navigation (Prev/Next) to preview area
- Render each page of merged document separately
- Store all page images, navigate between them

## Implementation Order
1. Phase 1 first (unblocks the entire merge tab)
2. Phase 2 (enables real PDF output)
3. Phase 4 (multi-page preview)
4. Phase 3 (additive editing — most complex, can iterate)

## Dependencies to Add
- PDFsharp-MigraDoc (or PdfSharpCore) for PDF writing
- No new deps needed for DOCX — OpenXml + SkiaSharp already present
