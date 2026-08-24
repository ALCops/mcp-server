using System.Runtime.InteropServices;

namespace ALCops.Mcp.Services;

public sealed class AlMcpLocator
{
    private readonly string? _explicitPath;
    private string? _cachedPath;

    public AlMcpLocator(string? explicitPath = null)
    {
        _explicitPath = explicitPath;
    }

    public string? GetAlMcpPath()
    {
        if (_cachedPath is not null)
            return _cachedPath;

        // 1. Explicit path (from --almcp-path CLI arg)
        if (_explicitPath is not null)
        {
            if (File.Exists(_explicitPath))
                return _cachedPath = _explicitPath;
            return null;
        }

        // 2. ALMCP_PATH environment variable
        var envPath = Environment.GetEnvironmentVariable("ALMCP_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return _cachedPath = envPath;

        // 3. Auto-discover from AL Language VS Code extension
        var extensionDir = FindAlExtensionDirectory();
        if (extensionDir is not null)
        {
            var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "almcp.exe" : "almcp";
            var almcpPath = Path.Combine(extensionDir, "bin", exeName);
            if (File.Exists(almcpPath))
                return _cachedPath = almcpPath;
        }

        return null;
    }

    private static string? FindAlExtensionDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return null;

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
