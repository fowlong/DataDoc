using System.Text.Json.Serialization;

namespace CaptureFlow.Core.Models;

public class CaptureBox
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Field";
    public string OutputHeader { get; set; } = "Column";
    public int PageIndex { get; set; }
    public NormalisedRect Rect { get; set; } = new(0.1, 0.1, 0.2, 0.05);
    public ExtractionMode ExtractionMode { get; set; } = ExtractionMode.NativeWithOcrFallback;
    public RowTargetMode RowTargetMode { get; set; } = RowTargetMode.DocumentRow;
    public string? RepeatGroupId { get; set; }
    public List<TransformRule> TransformRules { get; set; } = [];
    public List<ValidationRule> ValidationRules { get; set; } = [];
    public AnchorConfig? AnchorConfig { get; set; }
    public string? DefaultValue { get; set; }
    public string? FallbackValue { get; set; }
    public string? Notes { get; set; }
    public bool Enabled { get; set; } = true;
    public string? ColorHex { get; set; }
    public int SortOrder { get; set; }
    /// <summary>
    /// Which CSV output this field belongs to. Default is "CSV 1".
    /// Multiple groups produce separate extraction result sets.
    /// </summary>
    public string CsvGroup { get; set; } = "CSV 1";
}

public class AnchorConfig
{
    public string? AnchorText { get; set; }
    public NormalisedRect? SearchRegion { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public bool Enabled { get; set; }
}

public class TransformRule
{
    public string Type { get; set; } = "";
    public string? Parameter { get; set; }
    public string? Parameter2 { get; set; }
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
}

public class ValidationRule
{
    public string Type { get; set; } = "";
    public string? Parameter { get; set; }
    public string? Message { get; set; }
    public ValidationSeverity Severity { get; set; } = ValidationSeverity.Error;
    public bool Enabled { get; set; } = true;
}
