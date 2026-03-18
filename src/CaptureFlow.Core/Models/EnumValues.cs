namespace CaptureFlow.Core.Models;

public static class ExtractionModeValues
{
    public static ExtractionMode[] All =>
    [
        ExtractionMode.NativeOnly,
        ExtractionMode.OcrOnly,
        ExtractionMode.NativeWithOcrFallback,
        ExtractionMode.Both
    ];
}

public static class RowTargetModeValues
{
    public static RowTargetMode[] All =>
    [
        RowTargetMode.DocumentRow,
        RowTargetMode.RepeatGroupRow,
        RowTargetMode.StartNewRow,
        RowTargetMode.AppendToPrevious,
        RowTargetMode.CopyToAllRows
    ];
}
