# CaptureFlow CSV - Architecture

## Overview

CaptureFlow CSV is a Windows desktop application for document-to-CSV extraction and CSV-to-document merge generation. Built on .NET 8 with WPF, it follows MVVM architecture with clean separation between UI, business logic, and data persistence.

## Solution Structure

```
CaptureFlow.sln
├── src/
│   ├── CaptureFlow.Core/           # Business logic, models, services
│   │   ├── Models/                  # Domain models (SourceDocument, CaptureBox, Templates, etc.)
│   │   ├── Interfaces/              # Service contracts (IDocumentAdapter, ITemplateRepository, etc.)
│   │   ├── Services/
│   │   │   ├── Adapters/            # Per-format document adapters (PDF, DOCX, Email, etc.)
│   │   │   ├── Extraction/          # Text extraction pipeline, batch processing, CSV export
│   │   │   ├── Merge/               # CSV-to-document merge (DOCX, PDF)
│   │   │   ├── OCR/                 # Tesseract OCR integration
│   │   │   ├── Transforms/          # Text cleanup/transformation rules
│   │   │   └── Validation/          # Per-field validation rules
│   │   └── Utilities/               # File type detection, helpers
│   │
│   ├── CaptureFlow.App/            # WPF application (UI layer)
│   │   ├── Views/                   # XAML views and panels
│   │   ├── ViewModels/              # MVVM view models
│   │   ├── Controls/                # Custom controls (DocumentPreviewCanvas)
│   │   ├── Converters/              # WPF value converters
│   │   └── Resources/               # Themes, styles, assets
│   │
│   └── CaptureFlow.Data/           # Persistence layer
│       └── Repositories/            # JSON-based template and project storage
│
├── tests/
│   └── CaptureFlow.Core.Tests/     # Unit tests
│
├── docs/                            # Documentation
├── samples/                         # Sample templates and documents
└── packaging/                       # MSIX packaging notes
```

## Core Abstractions

### Normalised Document Model

Every file format adapts into a unified model:

```
SourceDocument
  └── DocumentPage[]
       ├── OriginalWidth/Height
       ├── NativeTextFragments[]     (positioned text from source)
       ├── OcrTextFragments[]        (OCR results on demand)
       ├── PreviewImagePng           (rendered preview)
       └── PlainText                 (fallback)
```

All coordinates are normalised (0.0–1.0) relative to page dimensions.

### Document Adapter Pattern

Each file format has an adapter implementing `IDocumentAdapter`:

- `PdfDocumentAdapter` — PdfPig for text extraction, Docnet for rendering
- `DocxDocumentAdapter` — OpenXml SDK
- `EmailDocumentAdapter` — MimeKit (EML) + MsgReader (MSG)
- `PlainTextAdapter` — TXT, RTF, HTML
- `ImageDocumentAdapter` — SkiaSharp for images, OCR-only path

### Extraction Pipeline

1. Load document via adapter
2. Normalise into page model
3. Render preview
4. Apply template (capture boxes)
5. Extract text intersecting each box's rectangle
6. Apply transform rules
7. Validate results
8. Build row objects
9. Display in editable grid

### Row Model

Fields can target:
- **DocumentRow** — one value per document
- **RepeatGroupRow** — value per repeated row in a region
- **StartNewRow** — begins a new output row
- **AppendToPrevious** — appends to last row
- **CopyToAllRows** — copies value to every row

### Template System

- **Page Template** — reusable set of capture boxes for a page layout
- **Document Template** — composed of page template assignments + document-level fields + repeat groups

## Technology Stack

| Component | Technology | License |
|-----------|-----------|---------|
| UI Framework | WPF (.NET 8) | MIT |
| MVVM | CommunityToolkit.Mvvm | MIT |
| PDF Text | PdfPig | Apache 2.0 |
| PDF Render | Docnet.Core | MIT |
| DOCX | DocumentFormat.OpenXml | MIT |
| Email (EML) | MimeKit | MIT |
| Email (MSG) | MsgReader | MIT |
| OCR | Tesseract.NET | Apache 2.0 |
| Image | SkiaSharp | MIT |
| CSV | CsvHelper | MS-PL/Apache 2.0 |
| Storage | JSON files (System.Text.Json) | Built-in |
| DI | Microsoft.Extensions.DependencyInjection | MIT |

All libraries use permissive licenses suitable for closed-source commercial distribution.

## Data Storage

Application data stored under `%LOCALAPPDATA%\CaptureFlow\`:
- `Templates/Pages/` — Page template JSON files
- `Templates/Documents/` — Document template JSON files
- `Projects/` — Project JSON files

## Key Design Decisions

1. **WPF over WinUI 3** — More mature DataGrid, stable rendering, better library ecosystem, still MSIX-packageable for Store
2. **JSON over SQLite for v1** — Simpler, human-readable, easy to backup/migrate. Repository interfaces allow switching to SQLite later
3. **PdfPig over iTextSharp** — Apache 2.0 licensed, no AGPL concerns
4. **Normalised coordinates** — All box positions stored as 0.0–1.0 fractions, making templates resolution-independent
5. **Adapter pattern** — Clean extension point for new file formats without touching core logic
