namespace ALCops.Mcp.Services;

public sealed class AlExtensionLocator
{
    private string? _cachedAnalyzersPath;

    /// <summary>
    /// Finds the AL Language extension's Analyzers directory.
    /// This is where ${CodeCop}, ${UICop}, ${analyzerFolder}, etc. resolve to.
    /// </summary>
    public string? GetAnalyzersPath()
    {
        if (_cachedAnalyzersPath is not null)
            return _cachedAnalyzersPath;

        var extensionDir = FindAlExtensionDirectory();
        if (extensionDir is null)
            return null;

        var analyzersPath = Path.Combine(extensionDir, "bin", "Analyzers");
        if (Directory.Exists(analyzersPath))
            return _cachedAnalyzersPath = analyzersPath;

        return null;
    }

    private static string? FindAlExtensionDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return null;

        // Search common VS Code extension locations
        string[] extensionRoots =
        [
            Path.Combine(home, ".vscode", "extensions"),
            Path.Combine(home, ".vscode-insiders", "extensions"),
            Path.Combine(home, ".vscode-server", "extensions"),
        ];

        string? bestMatch = null;
        Version? bestVersion = null;

        foreach (var root in extensionRoots)
        {
            if (!Directory.Exists(root))
                continue;

            try
            {
                foreach (var dir in Directory.GetDirectories(root, "ms-dynamics-smb.al-*"))
                {
                    var dirName = Path.GetFileName(dir);
                    var versionStr = dirName["ms-dynamics-smb.al-".Length..];
                    if (Version.TryParse(versionStr, out var version) && (bestVersion is null || version > bestVersion))
                    {
                        bestVersion = version;
                        bestMatch = dir;
                    }
                }
            }
            catch
            {
                // Permission issues, etc.
            }
        }

        return bestMatch;
    }
}
