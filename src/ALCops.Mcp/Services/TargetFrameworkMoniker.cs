using System.Reflection;
using System.Runtime.Versioning;

namespace ALCops.Mcp.Services;

/// <summary>
/// TFM utilities for matching NuGet <c>lib/</c> folders to the installed DevTools runtime.
/// </summary>
internal static class TargetFrameworkMoniker
{
    /// <summary>
    /// Converts a long framework name to its short form:
    /// <c>.NETCoreApp,Version=v10.0</c> → <c>net10.0</c>,
    /// <c>.NETStandard,Version=v2.1</c> → <c>netstandard2.1</c>.
    /// </summary>
    public static string? ShortName(string? frameworkName)
    {
        if (string.IsNullOrEmpty(frameworkName))
            return null;

        const string corePrefix = ".NETCoreApp,Version=v";
        const string standardPrefix = ".NETStandard,Version=v";

        if (frameworkName.StartsWith(corePrefix, StringComparison.OrdinalIgnoreCase))
            return "net" + frameworkName[corePrefix.Length..];

        if (frameworkName.StartsWith(standardPrefix, StringComparison.OrdinalIgnoreCase))
            return "netstandard" + frameworkName[standardPrefix.Length..];

        return null;
    }

    /// <summary>
    /// Detects the TFM of the installed BC DevTools from the loaded <c>Nav.CodeAnalysis</c> assembly,
    /// falling back to the TFM segment of the tools directory path.
    /// </summary>
    public static string DetectDevToolsTfm(string toolsDirectory)
    {
        try
        {
            var attr = typeof(Microsoft.Dynamics.Nav.CodeAnalysis.Compilation)
                .Assembly
                .GetCustomAttribute<TargetFrameworkAttribute>();

            if (attr is not null)
            {
                var short_ = ShortName(attr.FrameworkName);
                if (short_ is not null)
                    return short_;
            }
        }
        catch
        {
            // Non-critical: fall through to path-based detection.
        }

        // Fallback: extract from the tools directory path (e.g. .../tools/net10.0/any/).
        var segments = toolsDirectory.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (segments[i].StartsWith("net", StringComparison.OrdinalIgnoreCase) &&
                segments[i].Contains('.'))
                return segments[i];
        }

        return "net10.0";
    }

    /// <summary>
    /// Picks the best <c>lib/</c> folder from a NuGet package for the given target TFM.
    /// Ports npm <c>@alcops/core</c>'s <c>findMatchingTfmFolder</c> exactly:
    /// exact → for <c>netN.0</c> targets try <c>net{N-1}.0</c> down to <c>net6.0</c> →
    /// <c>netstandard2.1</c>; for <c>netstandardX.Y</c> the lowest higher-or-equal minor.
    /// </summary>
    public static string? FindBestLibFolder(IEnumerable<string> available, string target)
    {
        var set = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);

        if (set.Count == 0)
            return null;

        // Exact match.
        if (set.Contains(target))
            return target;

        // For netN.0 targets: walk downward from N-1 to 6, then fall back to netstandard2.1.
        if (TryParseNetVersion(target, out var major))
        {
            for (var n = major - 1; n >= 6; n--)
            {
                var candidate = $"net{n}.0";
                if (set.Contains(candidate))
                    return candidate;
            }

            if (set.Contains("netstandard2.1"))
                return "netstandard2.1";

            return null;
        }

        // For netstandardX.Y targets: lowest higher-or-equal minor wins.
        if (TryParseNetstandardMinor(target, out var targetMinor))
        {
            string? best = null;
            int bestMinor = int.MaxValue;

            foreach (var folder in set)
            {
                if (TryParseNetstandardMinor(folder, out var minor) && minor >= targetMinor && minor < bestMinor)
                {
                    best = folder;
                    bestMinor = minor;
                }
            }

            return best;
        }

        return null;
    }

    private static bool TryParseNetVersion(string tfm, out int major)
    {
        major = 0;
        if (!tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase) ||
            tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ||
            tfm.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = tfm[3..];
        var dotIndex = rest.IndexOf('.');
        var versionPart = dotIndex >= 0 ? rest[..dotIndex] : rest;
        return int.TryParse(versionPart, out major) && major >= 5;
    }

    private static bool TryParseNetstandardMinor(string tfm, out int minor)
    {
        minor = 0;
        if (!tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = tfm["netstandard".Length..];
        var dotIndex = rest.IndexOf('.');
        if (dotIndex < 0)
            return false;

        return int.TryParse(rest[(dotIndex + 1)..], out minor);
    }
}
