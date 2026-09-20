using System.Text.Json;

namespace ALCops.Mcp.Services;

/// <summary>
/// Parses NuGet's flat-container <c>index.json</c> version list.
/// </summary>
internal static class NuGetVersions
{
    public static (string? Latest, string? Prerelease) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("versions", out var versions) ||
            versions.ValueKind != JsonValueKind.Array)
            return (null, null);

        SemanticVersion? latest = null;
        // `newest` is the highest version overall, stable or not; a stable release outranks its own prereleases, so `--alcops-analyzers prerelease` never picks an rc that already shipped as stable.
        SemanticVersion? newest = null;

        foreach (var element in versions.EnumerateArray())
        {
            var raw = element.GetString();
            if (!SemanticVersion.TryParse(raw, out var v))
                continue;

            if (v.IsStable && (latest is null || v.CompareTo(latest) > 0))
                latest = v;

            if (newest is null || v.CompareTo(newest) > 0)
                newest = v;
        }

        return (latest?.Raw, newest?.Raw);
    }
}
