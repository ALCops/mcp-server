using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ALCops.Mcp.Models;
using ModelContextProtocol.Protocol;

namespace ALCops.Mcp.Services;

public sealed record RawCompileDiagnostic(string Severity, string Code, string? Location, string Description);

public readonly record struct ParsedLocation(string? Path, int? Line, int? Column);

public sealed record AnalyzeFilter(
    string? FilePath,
    string? FolderPath,
    string? ProjectPath,
    bool IncludeUnlocated,
    IReadOnlySet<string>? Severities,
    IReadOnlySet<string>? Analyzers,
    IReadOnlySet<string>? RuleIds);

public static partial class CompileDiagnosticsParser
{
    public static (IReadOnlyList<RawCompileDiagnostic> Diagnostics, string? Message, bool Succeeded) Parse(CallToolResult result)
    {
        if (result.StructuredContent is JsonNode structured)
            return Parse(structured.ToJsonString());

        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            var text = block.Text?.Trim();
            if (text is not null && text.StartsWith('{'))
            {
                try
                {
                    return Parse(text);
                }
                catch (JsonException) { }
            }
        }

        var preview = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        if (preview.Length > 200) preview = preview[..200];
        throw new InvalidOperationException($"almcp returned no JSON payload for al_compile: {preview}");
    }

    public static (IReadOnlyList<RawCompileDiagnostic> Diagnostics, string? Message, bool Succeeded) Parse(string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var dto = JsonSerializer.Deserialize<CompileResultDto>(json, options);

        var diagnostics = new List<RawCompileDiagnostic>();
        if (dto?.Diagnostics is not null)
        {
            foreach (var d in dto.Diagnostics)
            {
                if (string.IsNullOrEmpty(d.Code))
                    continue;

                var severity = ResolveSeverity(d.Severity);
                diagnostics.Add(new RawCompileDiagnostic(severity, d.Code!, d.Location, d.Description ?? ""));
            }
        }

        var message = string.IsNullOrWhiteSpace(dto?.Message) ? null : dto.Message;
        var succeeded = dto?.Succeeded ?? true;

        return (diagnostics, message, succeeded);
    }

    public static ParsedLocation ParseLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return default;

        var match = LocationRegex().Match(location);
        if (!match.Success)
            return default;

        var path = match.Groups["path"].Value;
        if (path.Length == 0)
            return default;

        if (!int.TryParse(match.Groups["line"].ValueSpan, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var line))
            return default;

        if (!int.TryParse(match.Groups["col"].ValueSpan, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var column))
            return default;

        return new ParsedLocation(path, line, column);
    }

    [GeneratedRegex(@"^(?!None\b)[A-Za-z]+\((?<path>.*)@(?<line>\d+):(?<col>\d+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex LocationRegex();

    public static IReadOnlyList<string> ExtractWarnings(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return [];

        var lines = message.Split(["\r\n", "\n"], StringSplitOptions.None);
        var result = new List<string>();
        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith("[Warning]", StringComparison.Ordinal))
            {
                var rest = trimmed["[Warning]".Length..].TrimStart();
                if (rest.Length > 0)
                    result.Add(rest);
            }
            else
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    public static bool IsCompilerDiagnostic(string code) => CompilerDiagnosticRegex().IsMatch(code);

    [GeneratedRegex(@"^AL\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex CompilerDiagnosticRegex();

    public static IReadOnlyList<AnalyzeDiagnostic> Enrich(
        IReadOnlyList<RawCompileDiagnostic> raw,
        Func<string, string> copNameOf,
        Func<string, bool> hasFixOf,
        Func<string, string> normalizePath)
    {
        var result = new List<AnalyzeDiagnostic>(raw.Count);
        foreach (var d in raw)
        {
            var loc = ParseLocation(d.Location);
            var filePath = loc.Path is not null ? normalizePath(loc.Path) : null;

            string analyzer;
            bool hasFix;
            if (IsCompilerDiagnostic(d.Code))
            {
                analyzer = "Compiler";
                hasFix = false;
            }
            else
            {
                analyzer = copNameOf(d.Code);
                hasFix = hasFixOf(d.Code);
            }

            result.Add(new AnalyzeDiagnostic(filePath, loc.Line, loc.Column, d.Code, d.Severity, d.Description, analyzer, hasFix));
        }

        return result;
    }

    public static (IReadOnlyList<AnalyzeDiagnostic> Filtered, int DroppedUnlocated) Filter(IReadOnlyList<AnalyzeDiagnostic> all, AnalyzeFilter filter)
    {
        var result = new List<AnalyzeDiagnostic>();
        var droppedUnlocated = 0;

        foreach (var d in all)
        {
            if (d.FilePath is null)
            {
                if (!filter.IncludeUnlocated)
                {
                    droppedUnlocated++;
                    continue;
                }
            }
            else
            {
                if (filter.FilePath is not null &&
                    !d.FilePath.Equals(filter.FilePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (filter.FolderPath is not null &&
                    !d.FilePath.StartsWith(filter.FolderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (filter.ProjectPath is not null &&
                    !d.FilePath.StartsWith(filter.ProjectPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (filter.Severities is not null && !filter.Severities.Contains(d.Severity))
                continue;

            if (filter.Analyzers is not null && !filter.Analyzers.Contains(d.Analyzer))
                continue;

            if (filter.RuleIds is not null && !filter.RuleIds.Contains(d.Id))
                continue;

            result.Add(d);
        }

        return (result, droppedUnlocated);
    }

    public static string? FindContainingProject(string path, IReadOnlyList<string> projectDirectories)
    {
        string? best = null;
        var bestLen = -1;

        foreach (var project in projectDirectories)
        {
            if (project.Length <= bestLen)
                continue;

            if (path.Equals(project, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                best = project;
                bestLen = project.Length;
            }
        }

        return best;
    }

    public static IReadOnlyList<AnalyzeDiagnostic> Sort(IEnumerable<AnalyzeDiagnostic> diagnostics) =>
        diagnostics
            .OrderBy(d => d.FilePath ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Line ?? 0)
            .ThenBy(d => d.Column ?? 0)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<string> BuildWarnings(bool succeeded, int rawCount, int filteredCount, int droppedUnlocated)
    {
        var warnings = new List<string>();

        if (!succeeded)
            warnings.Add($"al_compile reported succeeded=false: {rawCount} diagnostics workspace-wide, {filteredCount} after filtering.");

        if (droppedUnlocated > 0)
            warnings.Add($"{droppedUnlocated} diagnostic(s) without a file location were excluded by the scope filter; call analyze without scope arguments to see them.");

        return warnings;
    }

    public static AnalyzeResult Build(string project, IReadOnlyList<AnalyzeDiagnostic> filteredSorted, int limit, IReadOnlyList<string> warnings)
    {
        var totalCount = filteredSorted.Count;
        var taken = totalCount <= limit ? filteredSorted : filteredSorted.Take(limit).ToList();
        var count = taken.Count;
        var truncated = totalCount > count;

        var bySeverity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byAnalyzer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in filteredSorted)
        {
            bySeverity[d.Severity] = bySeverity.GetValueOrDefault(d.Severity) + 1;
            byAnalyzer[d.Analyzer] = byAnalyzer.GetValueOrDefault(d.Analyzer) + 1;
        }

        var summary = new AnalyzeSummary(bySeverity, byAnalyzer);
        var warningsList = warnings.Count == 0 ? null : warnings;

        return new AnalyzeResult(project, count, totalCount, truncated, summary, taken, warningsList);
    }

    private static string ResolveSeverity(JsonElement severity)
    {
        if (severity.ValueKind == JsonValueKind.String)
            return severity.GetString()!;

        if (severity.ValueKind == JsonValueKind.Number && severity.TryGetInt32(out var num))
        {
            return num switch
            {
                0 => "Hidden",
                1 => "Info",
                2 => "Warning",
                3 => "Error",
                _ => "Unknown",
            };
        }

        return "Unknown";
    }

    private sealed class CompileResultDto
    {
        public bool? Succeeded { get; set; }
        public List<DiagnosticDto>? Diagnostics { get; set; }
        public string? Message { get; set; }
    }

    private sealed class DiagnosticDto
    {
        public JsonElement Severity { get; set; }
        public string? Code { get; set; }
        public string? Location { get; set; }
        public string? Description { get; set; }
    }
}
