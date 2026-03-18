namespace CaptureFlow.Core.Models;

public class Project
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled Project";
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public ProjectSettings Settings { get; set; } = new();
    public List<InputSource> InputSources { get; set; } = [];
    public string? SelectedDocumentTemplateId { get; set; }
    public string? SelectedPageTemplateId { get; set; }
    public OutputSettings OutputSettings { get; set; } = new();
}

public class ProjectSettings
{
    public ExtractionMode DefaultExtractionMode { get; set; } = ExtractionMode.NativeWithOcrFallback;
    public string DefaultCsvSeparator { get; set; } = ",";
    public bool IncludeSourceFileColumn { get; set; } = true;
    public bool IncludePageColumn { get; set; } = true;
}

public class InputSource
{
    public string Path { get; set; } = "";
    public bool IsFolder { get; set; }
    public List<SupportedFileType> IncludedFileTypes { get; set; } = [];
}

public class OutputSettings
{
    public string? OutputDirectory { get; set; }
    public string CsvSeparator { get; set; } = ",";
    public string CsvEncoding { get; set; } = "UTF-8";
    public bool IncludeHeaders { get; set; } = true;
    public string? FileNamePattern { get; set; }
}

public class MergeProject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Merge Project";
    public string? CsvFilePath { get; set; }
    public string? TemplateFilePath { get; set; }
    public MergeOutputFormat OutputFormat { get; set; } = MergeOutputFormat.Docx;
    public string? OutputDirectory { get; set; }
    public string FileNamePattern { get; set; } = "output_{{RowNumber}}";
    public List<MergeFieldMapping> FieldMappings { get; set; } = [];
}

public class MergeFieldMapping
{
    public string CsvHeader { get; set; } = "";
    public string TemplatePlaceholder { get; set; } = "";
}
