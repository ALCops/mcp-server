using System.Runtime.InteropServices;

namespace ALCops.Mcp.Services;

internal static class DotnetHost
{
    private static readonly string ExeName =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";

    public static string Resolve() =>
        Resolve(Environment.GetEnvironmentVariable, Environment.ProcessPath,
            Environment.GetEnvironmentVariable("PATH"));

    internal static string Resolve(
        Func<string, string?> getEnv,
        string? processPath,
        string? pathVar)
    {
        var fromHostPath = getEnv("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromHostPath) && File.Exists(fromHostPath))
            return fromHostPath;

        if (processPath is not null &&
            Path.GetFileName(processPath).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
            return processPath;

        foreach (var envVar in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64" })
        {
            var root = getEnv(envVar);
            if (string.IsNullOrEmpty(root))
                continue;
            var candidate = Path.Combine(root, ExeName);
            if (File.Exists(candidate))
                return candidate;
        }

        if (!string.IsNullOrEmpty(pathVar))
        {
            var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
            foreach (var dir in pathVar.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim(), ExeName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return "dotnet";
    }
}
