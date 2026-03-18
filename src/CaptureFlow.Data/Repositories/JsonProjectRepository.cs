using System.Text.Json;
using System.Text.Json.Serialization;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaptureFlow.Data.Repositories;

public class JsonProjectRepository : IProjectRepository
{
    private readonly string _projectsDir;
    private readonly ILogger<JsonProjectRepository> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonProjectRepository(ILogger<JsonProjectRepository> logger)
    {
        _logger = logger;
        _projectsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CaptureFlow", "Projects");
        Directory.CreateDirectory(_projectsDir);
    }

    public async Task<IReadOnlyList<Project>> GetRecentProjectsAsync(int count = 10, CancellationToken ct = default)
    {
        var projects = new List<Project>();
        foreach (var file in Directory.EnumerateFiles(_projectsDir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var project = JsonSerializer.Deserialize<Project>(json, JsonOptions);
                if (project != null) projects.Add(project);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load project {File}", file);
            }
        }
        return projects.OrderByDescending(p => p.ModifiedUtc).Take(count).ToList();
    }

    public async Task<Project?> GetProjectAsync(string id, CancellationToken ct = default)
    {
        var path = Path.Combine(_projectsDir, $"{id}.json");
        if (!File.Exists(path)) return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<Project>(json, JsonOptions);
    }

    public async Task SaveProjectAsync(Project project, CancellationToken ct = default)
    {
        project.ModifiedUtc = DateTime.UtcNow;
        var path = Path.Combine(_projectsDir, $"{project.Id}.json");
        var json = JsonSerializer.Serialize(project, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    public Task DeleteProjectAsync(string id, CancellationToken ct = default)
    {
        var path = Path.Combine(_projectsDir, $"{id}.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
