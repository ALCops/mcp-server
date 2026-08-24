using System.Text.Json;

namespace ALCops.Mcp.Services;

public enum AnalyzerSpecKind
{
    WellKnownBcCop,
    AnalyzerFolderRelative,
    DllPath
}

public sealed class AnalyzerSpec
{
    public AnalyzerSpecKind Kind { get; }
    public string RawValue { get; }
    public string? CopName { get; }
    public string? RelativeDllName { get; }

    private AnalyzerSpec(AnalyzerSpecKind kind, string rawValue, string? copName = null, string? relativeDllName = null)
    {
        Kind = kind;
        RawValue = rawValue;
        CopName = copName;
        RelativeDllName = relativeDllName;
    }

    private static readonly Dictionary<string, (string DllName, string CopName)> WellKnownCops = new(StringComparer.OrdinalIgnoreCase)
    {
        ["${CodeCop}"] = ("Microsoft.Dynamics.Nav.CodeCop.dll", "CodeCop"),
        ["${UICop}"] = ("Microsoft.Dynamics.Nav.UICop.dll", "UICop"),
        ["${PerTenantExtensionCop}"] = ("Microsoft.Dynamics.Nav.PerTenantExtensionCop.dll", "PerTenantExtensionCop"),
        ["${AppSourceCop}"] = ("Microsoft.Dynamics.Nav.AppSourceCop.dll", "AppSourceCop"),
    };

    public static AnalyzerSpec Parse(string value)
    {
        var trimmed = value.Trim();

        if (WellKnownCops.TryGetValue(trimmed, out var cop))
            return new AnalyzerSpec(AnalyzerSpecKind.WellKnownBcCop, trimmed, copName: cop.CopName, relativeDllName: cop.DllName);

        if (trimmed.StartsWith("${analyzerFolder}", StringComparison.OrdinalIgnoreCase))
        {
            var dllName = trimmed["${analyzerFolder}".Length..];
            return new AnalyzerSpec(AnalyzerSpecKind.AnalyzerFolderRelative, trimmed, relativeDllName: dllName);
        }

        return new AnalyzerSpec(AnalyzerSpecKind.DllPath, trimmed);
    }

    /// <summary>
    /// Parses the <c>analyzers</c> tool parameter — a JSON array of specs such as
    /// <c>["${CodeCop}","${UICop}"]</c>. Returns null for null or malformed input, which callers
    /// treat as "fall back to the project's own configuration".
    /// </summary>
    public static IReadOnlyList<string>? ParseJsonArray(string? analyzers)
    {
        if (analyzers is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<string>>(analyzers);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string GetDllFileName() => Kind switch
    {
        AnalyzerSpecKind.WellKnownBcCop => RelativeDllName!,
        AnalyzerSpecKind.AnalyzerFolderRelative => RelativeDllName!,
        AnalyzerSpecKind.DllPath => Path.GetFileName(RawValue),
        _ => throw new InvalidOperationException($"Unknown spec kind: {Kind}")
    };
}
