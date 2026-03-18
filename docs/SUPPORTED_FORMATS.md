# Supported Formats & Known Limitations

## File Format Support

### Tier 1 — Full Support (v1)

| Format | Extension | Text Extraction | Preview | Notes |
|--------|-----------|----------------|---------|-------|
| PDF | .pdf | Native text + OCR fallback | Rendered pages | Primary format. Best support. |
| DOCX | .docx | Full paragraph text | Rendered text preview | No embedded image extraction yet |
| TXT | .txt | Full text | Text preview | Single synthetic page per ~60 lines |
| HTML | .html, .htm | Stripped HTML tags | Text preview | Basic tag stripping, no CSS rendering |

### Tier 2 — Good Support (v1)

| Format | Extension | Text Extraction | Preview | Notes |
|--------|-----------|----------------|---------|-------|
| EML | .eml | Subject, from, to, body | Text preview | MIME parsing via MimeKit |
| MSG | .msg | Subject, from, to, body | Text preview | Outlook format via MsgReader |
| RTF | .rtf | Basic text extraction | Text preview | RTF control codes stripped |
| PNG | .png | OCR only | Image display | Requires Tesseract data files |
| JPG/JPEG | .jpg, .jpeg | OCR only | Image display | Requires Tesseract data files |
| TIFF | .tiff, .tif | OCR only | Image display | Multi-page TIFF: first page only |
| BMP | .bmp | OCR only | Image display | Requires Tesseract data files |

### Deferred / Limited (future)

| Format | Extension | Status |
|--------|-----------|--------|
| DOC | .doc | Not supported (legacy binary format) |
| ODT | .odt | Not supported |
| XPS | .xps | Not supported |
| XML | .xml | Not supported |

## Known Limitations

### PDF
- Scanned PDFs with no text layer require OCR (Tesseract must be installed)
- Complex PDF layouts (multi-column, rotated text) may extract in unexpected order
- PDF form fields are read for merge output but not for extraction input

### DOCX
- Preview is text-based, not a pixel-perfect layout rendering
- Embedded images are not rendered in preview
- Complex formatting (columns, text boxes) shown as linear text

### Email
- Attachments are listed in metadata but not extracted for processing
- HTML email bodies have tags stripped — formatting is lost
- MSG format requires Windows (COM interop)

### OCR
- Requires Tesseract OCR engine and trained data files
- English (eng) is the default language
- OCR accuracy varies with image quality, resolution, and font
- Processing is significantly slower than native text extraction

### Merge (CSV to Documents)
- DOCX merge: full placeholder replacement support ({{FieldName}})
- PDF merge: limited to AcroForm field filling. Arbitrary text overlay on non-form PDFs is basic.
- Table row repetition in DOCX templates is not yet supported

### Batch Processing
- Very large batches (1000+ files) may consume significant memory
- Processing is parallelised but UI updates are throttled
- Files with errors are skipped; see error list for details

### General
- No cloud/network features — local files only
- No auto-update mechanism (intended for Store distribution)
- Template coordinates are resolution-independent but tied to page layout
- Undo/redo is session-only (not persisted)
