# CaptureFlow CSV

A Windows desktop application for document-to-CSV data extraction and CSV-to-document merge generation.

## What It Does

**Extract:** Open documents (PDF, DOCX, email, images, text), draw capture boxes over page regions, define output headers, and extract structured data to CSV.

**Batch:** Point at a folder of documents, apply a template, and extract data from all matching files into a unified CSV.

**Merge:** Load a CSV and a DOCX/PDF template with `{{placeholder}}` tokens, map fields, and bulk-generate one output document per row.

## Quick Start

### Prerequisites

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 (recommended) or `dotnet` CLI

### Build & Run

```bash
dotnet restore CaptureFlow.sln
dotnet build CaptureFlow.sln
dotnet run --project src/CaptureFlow.App/CaptureFlow.App.csproj
```

### OCR Support (Optional)

For OCR on scanned documents/images, install Tesseract and place `eng.traineddata` in a `tessdata` folder next to the executable.

## Project Structure

```
src/
  CaptureFlow.Core/     Core logic: models, adapters, extraction, merge
  CaptureFlow.App/      WPF desktop application
  CaptureFlow.Data/     Persistence (JSON repositories)
tests/
  CaptureFlow.Core.Tests/
docs/
  ARCHITECTURE.md        Detailed architecture documentation
  SUPPORTED_FORMATS.md   File format support and limitations
  PACKAGING.md           MSIX/Store packaging guide
samples/
  templates/             Sample extraction and merge templates
  documents/             Sample CSV data for testing
```

## Key Features

- Multi-format document support (PDF, DOCX, TXT, HTML, RTF, EML, MSG, images)
- Visual capture box overlay system with drag/resize/nudge
- Template save/load/import/export
- Repeat groups for multi-row extraction from single documents
- Configurable transform rules (trim, regex, date/number cleanup, etc.)
- Per-field validation (required, pattern, numeric, date)
- Editable results grid with sort/filter
- Batch processing with progress reporting
- CSV-to-DOCX/PDF merge with placeholder replacement
- Keyboard shortcuts (Ctrl+O, Ctrl+S, Ctrl+E, F5, arrow keys for box nudge)

## Architecture

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for detailed architecture documentation.

## License

Proprietary. All rights reserved.
