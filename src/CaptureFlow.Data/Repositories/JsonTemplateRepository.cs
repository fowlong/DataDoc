using System.Text.Json;
using System.Text.Json.Serialization;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaptureFlow.Data.Repositories;

public class JsonTemplateRepository : ITemplateRepository
{
    private readonly string _basePath;
    private readonly string _pageTemplatesDir;
    private readonly string _docTemplatesDir;
    private readonly ILogger<JsonTemplateRepository> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonTemplateRepository(ILogger<JsonTemplateRepository> logger)
    {
        _logger = logger;
        _basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CaptureFlow", "Templates");
        _pageTemplatesDir = Path.Combine(_basePath, "Pages");
        _docTemplatesDir = Path.Combine(_basePath, "Documents");

        Directory.CreateDirectory(_pageTemplatesDir);
        Directory.CreateDirectory(_docTemplatesDir);
    }

    public async Task<IReadOnlyList<PageTemplate>> GetAllPageTemplatesAsync(CancellationToken ct = default)
    {
        var templates = new List<PageTemplate>();
        foreach (var file in Directory.EnumerateFiles(_pageTemplatesDir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var template = JsonSerializer.Deserialize<PageTemplate>(json, JsonOptions);
                if (template != null) templates.Add(template);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load page template {File}", file);
            }
        }
        return templates.OrderByDescending(t => t.ModifiedUtc).ToList();
    }

    public async Task<PageTemplate?> GetPageTemplateAsync(string id, CancellationToken ct = default)
    {
        var path = Path.Combine(_pageTemplatesDir, $"{id}.json");
        if (!File.Exists(path)) return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<PageTemplate>(json, JsonOptions);
    }

    public async Task SavePageTemplateAsync(PageTemplate template, CancellationToken ct = default)
    {
        template.ModifiedUtc = DateTime.UtcNow;
        var path = Path.Combine(_pageTemplatesDir, $"{template.Id}.json");
        var json = JsonSerializer.Serialize(template, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
        _logger.LogInformation("Saved page template {Name} ({Id})", template.Name, template.Id);
    }

    public Task DeletePageTemplateAsync(string id, CancellationToken ct = default)
    {
        var path = Path.Combine(_pageTemplatesDir, $"{id}.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<DocumentTemplate>> GetAllDocumentTemplatesAsync(CancellationToken ct = default)
    {
        var templates = new List<DocumentTemplate>();
        foreach (var file in Directory.EnumerateFiles(_docTemplatesDir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var template = JsonSerializer.Deserialize<DocumentTemplate>(json, JsonOptions);
                if (template != null) templates.Add(template);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load document template {File}", file);
            }
        }
        return templates.OrderByDescending(t => t.ModifiedUtc).ToList();
    }

    public async Task<DocumentTemplate?> GetDocumentTemplateAsync(string id, CancellationToken ct = default)
    {
        var path = Path.Combine(_docTemplatesDir, $"{id}.json");
        if (!File.Exists(path)) return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<DocumentTemplate>(json, JsonOptions);
    }

    public async Task SaveDocumentTemplateAsync(DocumentTemplate template, CancellationToken ct = default)
    {
        template.ModifiedUtc = DateTime.UtcNow;
        var path = Path.Combine(_docTemplatesDir, $"{template.Id}.json");
        var json = JsonSerializer.Serialize(template, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
        _logger.LogInformation("Saved document template {Name} ({Id})", template.Name, template.Id);
    }

    public Task DeleteDocumentTemplateAsync(string id, CancellationToken ct = default)
    {
        var path = Path.Combine(_docTemplatesDir, $"{id}.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<string> ExportTemplateAsync(string templateId, bool isDocumentTemplate, CancellationToken ct = default)
    {
        var dir = isDocumentTemplate ? _docTemplatesDir : _pageTemplatesDir;
        var path = Path.Combine(dir, $"{templateId}.json");
        return await File.ReadAllTextAsync(path, ct);
    }

    public async Task<string> ImportTemplateAsync(string json, CancellationToken ct = default)
    {
        // Try to detect type
        if (json.Contains("\"pageAssignments\"", StringComparison.OrdinalIgnoreCase))
        {
            var template = JsonSerializer.Deserialize<DocumentTemplate>(json, JsonOptions);
            if (template != null)
            {
                var newTemplate = new DocumentTemplate
                {
                    Name = template.Name + " (imported)",
                    ApplicableFileTypes = template.ApplicableFileTypes,
                    PageAssignments = template.PageAssignments,
                    DocumentLevelFields = template.DocumentLevelFields,
                    RepeatGroups = template.RepeatGroups,
                    Notes = template.Notes
                };
                await SaveDocumentTemplateAsync(newTemplate, ct);
                return newTemplate.Id;
            }
        }
        else
        {
            var template = JsonSerializer.Deserialize<PageTemplate>(json, JsonOptions);
            if (template != null)
            {
                var newTemplate = new PageTemplate
                {
                    Name = template.Name + " (imported)",
                    ApplicableFileTypes = template.ApplicableFileTypes,
                    CaptureBoxes = template.CaptureBoxes,
                    RepeatGroups = template.RepeatGroups,
                    Notes = template.Notes
                };
                await SavePageTemplateAsync(newTemplate, ct);
                return newTemplate.Id;
            }
        }

        throw new InvalidOperationException("Could not parse template JSON");
    }
}
