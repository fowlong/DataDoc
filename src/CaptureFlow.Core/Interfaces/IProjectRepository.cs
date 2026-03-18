using CaptureFlow.Core.Models;

namespace CaptureFlow.Core.Interfaces;

public interface IProjectRepository
{
    Task<IReadOnlyList<Project>> GetRecentProjectsAsync(int count = 10, CancellationToken ct = default);
    Task<Project?> GetProjectAsync(string id, CancellationToken ct = default);
    Task SaveProjectAsync(Project project, CancellationToken ct = default);
    Task DeleteProjectAsync(string id, CancellationToken ct = default);
}
