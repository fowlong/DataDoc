namespace CaptureFlow.Core.Models;

public class RepeatGroup
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Repeat Group";
    public int PageIndex { get; set; }
    public NormalisedRect RegionRect { get; set; } = new(0, 0, 1, 1);
    public RowDetectionMode RowDetectionMode { get; set; } = RowDetectionMode.FixedHeight;
    public double? FixedRowHeight { get; set; }
    public int? ExpectedRowCount { get; set; }
    public string? RowSeparatorPattern { get; set; }
    public List<string> ChildFieldIds { get; set; } = [];
    public bool Enabled { get; set; } = true;
}
