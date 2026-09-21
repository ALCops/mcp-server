namespace ALCops.Mcp.Models;

public record AnalyzeDiagnostic(
    string? FilePath,
    int? Line,
    int? Column,
    string Id,
    string Severity,
    string Message,
    string Analyzer,
    bool HasFix);

public record AnalyzeSummary(
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByAnalyzer);

public record AnalyzeResult(
    string Project,
    int Count,
    int TotalCount,
    bool Truncated,
    AnalyzeSummary Summary,
    IReadOnlyList<AnalyzeDiagnostic> Diagnostics,
    IReadOnlyList<string>? Warnings);
