namespace CaptureFlow.Core.Models;

public enum SupportedFileType
{
    Pdf,
    Docx,
    Doc,
    Txt,
    Rtf,
    Html,
    Eml,
    Msg,
    Png,
    Jpg,
    Tiff,
    Bmp,
    Odt,
    Xps,
    Xml,
    Unknown
}

public enum ExtractionMode
{
    NativeOnly,
    OcrOnly,
    NativeWithOcrFallback,
    Both
}

public enum RowTargetMode
{
    DocumentRow,
    RepeatGroupRow,
    StartNewRow,
    AppendToPrevious,
    CopyToAllRows
}

public enum ProcessingState
{
    Pending,
    Loading,
    Loaded,
    Extracting,
    Extracted,
    Error,
    Skipped
}

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

public enum RowDetectionMode
{
    FixedHeight,
    DynamicByContent,
    Manual
}

public enum MergeOutputFormat
{
    Docx,
    Pdf
}
