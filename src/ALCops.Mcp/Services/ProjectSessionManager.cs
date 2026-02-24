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
    /// Gets an existing session or loads the project from disk.
    /// </summary>
    public async Task<ProjectSession> GetOrLoadProjectAsync(string projectPath, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(projectPath);

        if (_sessions.TryGetValue(normalizedPath, out var existing))
            return existing;

        var session = await _loader.LoadProjectAsync(normalizedPath, ct);
        _sessions[normalizedPath] = session;
        return session;
    }

    /// <summary>
    /// Reloads a project, discarding the cached session.
    /// </summary>
    public async Task<ProjectSession> ReloadProjectAsync(string projectPath, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(projectPath);

        if (_sessions.TryRemove(normalizedPath, out var old))
            old.Dispose();

        return await GetOrLoadProjectAsync(normalizedPath, ct);
    }

    /// <summary>
    /// Removes a cached project session.
    /// </summary>
    public void UnloadProject(string projectPath)
    {
        var normalizedPath = Path.GetFullPath(projectPath);
        if (_sessions.TryRemove(normalizedPath, out var session))
            session.Dispose();
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }
}
