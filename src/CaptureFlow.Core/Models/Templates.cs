namespace CaptureFlow.Core.Models;

public class PageTemplate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Page Template";
    public int Version { get; set; } = 1;
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public List<SupportedFileType> ApplicableFileTypes { get; set; } = [];
    public List<CaptureBox> CaptureBoxes { get; set; } = [];
    public List<RepeatGroup> RepeatGroups { get; set; } = [];
    public string? Notes { get; set; }
}

public class PageTemplateAssignment
{
    public string PageTemplateId { get; set; } = "";
    public int? PageIndex { get; set; }
    public string? PageRange { get; set; }
    public string? Condition { get; set; }
}

public class DocumentTemplate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Document Template";
    public int Version { get; set; } = 1;
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public List<SupportedFileType> ApplicableFileTypes { get; set; } = [];
    public List<PageTemplateAssignment> PageAssignments { get; set; } = [];
    public List<CaptureBox> DocumentLevelFields { get; set; } = [];
    public List<RepeatGroup> RepeatGroups { get; set; } = [];
    public string? Notes { get; set; }
}
