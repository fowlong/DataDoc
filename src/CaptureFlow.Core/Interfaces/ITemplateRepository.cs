using CaptureFlow.Core.Models;

namespace CaptureFlow.Core.Interfaces;

public interface ITemplateRepository
{
    Task<IReadOnlyList<PageTemplate>> GetAllPageTemplatesAsync(CancellationToken ct = default);
    Task<PageTemplate?> GetPageTemplateAsync(string id, CancellationToken ct = default);
    Task SavePageTemplateAsync(PageTemplate template, CancellationToken ct = default);
    Task DeletePageTemplateAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<DocumentTemplate>> GetAllDocumentTemplatesAsync(CancellationToken ct = default);
    Task<DocumentTemplate?> GetDocumentTemplateAsync(string id, CancellationToken ct = default);
    Task SaveDocumentTemplateAsync(DocumentTemplate template, CancellationToken ct = default);
    Task DeleteDocumentTemplateAsync(string id, CancellationToken ct = default);

    Task<string> ExportTemplateAsync(string templateId, bool isDocumentTemplate, CancellationToken ct = default);
    Task<string> ImportTemplateAsync(string json, CancellationToken ct = default);
}
