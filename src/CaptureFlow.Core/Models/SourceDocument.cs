namespace CaptureFlow.Core.Models;

/// <summary>
/// Normalised document abstraction. Any file type adapts into this model.
/// </summary>
public class SourceDocument
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public SupportedFileType FileType { get; init; }
    public int PageCount => Pages.Count;
    public List<DocumentPage> Pages { get; init; } = [];
    public DocumentMetadata Metadata { get; init; } = new();
    public ProcessingState State { get; set; } = ProcessingState.Pending;
    public string? ErrorMessage { get; set; }
    public long FileSizeBytes { get; set; }
}

public class DocumentMetadata
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }

    // Email-specific
    public string? Sender { get; set; }
    public string? Recipients { get; set; }
    public DateTime? SentDate { get; set; }
}
