namespace ALCops.Mcp.Services;

public sealed class DevToolsLocator
{
    private readonly string? _bootstrapPath;
    private string? _cachedPath;

    public DevToolsLocator(string? bootstrapPath = null)
    {
        _bootstrapPath = bootstrapPath;
    }

    public string GetDevToolsPath()
    {
        if (_cachedPath is not null)
            return _cachedPath;

        // 0. Bootstrap-resolved path (from BcDevToolsBootstrap at startup)
        if (_bootstrapPath is not null)
        {
            if (ValidateDevToolsPath(_bootstrapPath))
                return _cachedPath = _bootstrapPath;

            // Bootstrap may have resolved to the net8.0/ subdir — check parent
            var parent = Path.GetDirectoryName(_bootstrapPath);
            if (parent is not null && ValidateDevToolsPath(parent))
                return _cachedPath = parent;
        }

        // 1. Environment variable
        var envPath = Environment.GetEnvironmentVariable("BCDEVELOPMENTTOOLSPATH");
        if (!string.IsNullOrEmpty(envPath) && ValidateDevToolsPath(envPath))
            return _cachedPath = envPath;

        // 2. Relative to executable (typical for deployed MCP server)
        //    From bin/Release/net8.0/ we need 5 levels up to reach the repo root:
        //    net8.0 → Release → bin → ALCops.Mcp → src → Analyzers/
        var exeDir = AppContext.BaseDirectory;
        var relativeToExe = Path.GetFullPath(
            Path.Combine(exeDir, "..", "..", "..", "..", "..", "Microsoft.Dynamics.BusinessCentral.Development.Tools"));
        if (ValidateDevToolsPath(relativeToExe))
            return _cachedPath = relativeToExe;

        // 3. Relative to current working directory (development scenario)
        var relativeToWorkDir = Path.GetFullPath("Microsoft.Dynamics.BusinessCentral.Development.Tools");
        if (ValidateDevToolsPath(relativeToWorkDir))
            return _cachedPath = relativeToWorkDir;

        throw new InvalidOperationException(
            "BC Development Tools not found. Set the BCDEVELOPMENTTOOLSPATH environment variable " +
            "or place the tools at Microsoft.Dynamics.BusinessCentral.Development.Tools/ relative to the repository root.");
    }

    private static bool ValidateDevToolsPath(string path)
    {
        return File.Exists(Path.Combine(path, "net8.0", "Microsoft.Dynamics.Nav.CodeAnalysis.dll"));
    }
}
