namespace ALCops.Mcp.Services;

/// <summary>
/// Project argument validation shared by the native tools. A failure is an <c>Invalid</c> error with
/// the returned message.
/// </summary>
internal static class ProjectScope
{
    private const string EmptyProjectListMessage =
        "No AL project available. Pass projectPath, or start the server from a folder " +
        "containing app.json (or use --projects).";

    /// <summary>
    /// For tools scoped to the projects this server was started with (<c>analyze</c>, <c>list_rules</c>):
    /// resolves <paramref name="projectPath"/>, or the primary project when it is null, and rejects a
    /// path that is not one of <see cref="WorkspaceStartupConfig.ProjectDirectories"/>. Returns the
    /// normalized project folder, or null with <paramref name="invalidMessage"/> set.
    /// </summary>
    internal static string? Resolve(WorkspaceStartupConfig config, string? projectPath, out string? invalidMessage)
    {
        var knownProjects = config.ProjectDirectories.Select(Normalize).ToList();

        if (knownProjects.Count == 0)
        {
            invalidMessage = EmptyProjectListMessage;
            return null;
        }

        if (projectPath is null)
        {
            invalidMessage = null;
            return knownProjects[0];
        }

        var normalized = Normalize(projectPath);
        if (!knownProjects.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            invalidMessage =
                $"'{projectPath}' is not one of the AL projects this server was started with: " +
                $"{string.Join(", ", knownProjects)}. Pass one of those, or restart the server with --projects.";
            return null;
        }

        invalidMessage = null;
        return normalized;
    }

    /// <summary>
    /// For the fix tools, which accept any AL project: the folder must exist and contain app.json.
    /// </summary>
    internal static bool RequireProjectFolder(string projectPath, out string? invalidMessage)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            invalidMessage = "projectPath is required: the absolute path of the AL project folder (contains app.json).";
            return false;
        }

        var full = Path.GetFullPath(projectPath);
        if (!Directory.Exists(full))
        {
            invalidMessage = $"projectPath '{projectPath}' does not exist. Pass the absolute path of the AL project folder (contains app.json).";
            return false;
        }

        if (!File.Exists(Path.Combine(full, "app.json")))
        {
            invalidMessage = $"projectPath '{projectPath}' contains no app.json. Pass the AL project folder itself, not a parent or child folder.";
            return false;
        }

        invalidMessage = null;
        return true;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
