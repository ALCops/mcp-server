using System.Collections.Concurrent;

namespace ALCops.Mcp.Services;

public sealed class ProjectSessionManager : IDisposable
{
    private readonly ProjectLoader _loader;
    private readonly ConcurrentDictionary<string, ProjectSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public ProjectSessionManager(ProjectLoader loader)
    {
        _loader = loader;
    }

    /// <summary>
    /// Gets an existing session (refreshing it from disk) or loads the project for the first time.
    /// </summary>
    // The pre-existing concurrent-first-load race (two first calls both load) is out of scope.
    public async Task<ProjectSession> GetOrLoadProjectAsync(string projectPath, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(projectPath);

        if (_sessions.TryGetValue(normalizedPath, out var existing))
        {
            await existing.RefreshFromDiskAsync(ct);
            return existing;
        }

        var session = await _loader.LoadProjectAsync(normalizedPath, ct);
        _sessions[normalizedPath] = session;
        return session;
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }
}
