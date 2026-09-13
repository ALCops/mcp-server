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

        string? latest = null;
        string? prerelease = null;

        Version? latestVersion = null;
        (Version? Base, string Raw)? prereleaseVersion = null;

        foreach (var element in versions.EnumerateArray())
        {
            var raw = element.GetString();
            if (string.IsNullOrEmpty(raw))
                continue;

            var parsed = ParseVersion(raw);
            if (parsed is null)
                continue;

            var (baseVersion, isStable) = parsed.Value;

            if (isStable && (latestVersion is null || CompareVersions(baseVersion, latestVersion) > 0))
            {
                latest = raw;
                latestVersion = baseVersion;
            }

            if (prereleaseVersion is null || CompareVersions(baseVersion, prereleaseVersion.Value.Base!) > 0 ||
                (CompareVersions(baseVersion, prereleaseVersion.Value.Base!) == 0 &&
                 StringComparer.OrdinalIgnoreCase.Compare(raw, prereleaseVersion.Value.Raw) > 0))
            {
                prerelease = raw;
                prereleaseVersion = (baseVersion, raw);
            }
        }

        return (latest, prerelease);
    }

    private static (Version Base, bool IsStable)? ParseVersion(string raw)
    {
        var dashIndex = raw.IndexOf('-');
        var isStable = dashIndex < 0;
        var basePart = isStable ? raw : raw[..dashIndex];

        return Version.TryParse(basePart, out var v)
            ? (v, isStable)
            : null;
    }

    private static int CompareVersions(Version? a, Version? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        return a.CompareTo(b);
    }
}
