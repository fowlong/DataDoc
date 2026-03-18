namespace CaptureFlow.Core.Models;

public class ExtractionResult
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string SourceDocumentId { get; init; }
    public required string SourceFileName { get; init; }
    public int PageIndex { get; init; }
    public required string Header { get; init; }
    public string? CaptureBoxId { get; init; }
    public string? RawValue { get; set; }
    public string? TransformedValue { get; set; }
    public double Confidence { get; set; } = 1.0;
    public ValidationState ValidationState { get; set; } = new();
    public int RowIndex { get; set; }
    public string? RepeatGroupId { get; set; }

    public string DisplayValue => TransformedValue ?? RawValue ?? "";
}

public class ValidationState
{
    public bool IsValid { get; set; } = true;
    public List<ValidationMessage> Messages { get; set; } = [];
}

public class ValidationMessage
{
    public required string Text { get; set; }
    public ValidationSeverity Severity { get; set; } = ValidationSeverity.Error;
    public string? RuleName { get; set; }
}

/// <summary>
/// One complete row of extracted data across all headers.
/// </summary>
public class ExtractionRow
{
    public int RowIndex { get; set; }
    public string SourceDocumentId { get; set; } = "";
    public string SourceFileName { get; set; } = "";
    public int? SourcePageIndex { get; set; }
    public Dictionary<string, ExtractionResult> Cells { get; set; } = new();
    public bool HasErrors => Cells.Values.Any(c => !c.ValidationState.IsValid);
}
