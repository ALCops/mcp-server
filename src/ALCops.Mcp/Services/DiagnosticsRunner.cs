using System.Collections.Immutable;
using ALCops.Mcp.Models;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;

namespace ALCops.Mcp.Services;

public sealed class DiagnosticsRunner
{
    private readonly AnalyzerRegistry _registry;

    public DiagnosticsRunner(AnalyzerRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Runs all registered analyzers against a project's compilation.
    /// </summary>
    public async Task<IReadOnlyList<DiagnosticResult>> RunAsync(
        ProjectSession session,
        string? filePath = null,
        IReadOnlySet<string>? copFilter = null,
        IReadOnlySet<string>? diagnosticIdFilter = null,
        DiagnosticSeverity? minSeverity = null,
        CancellationToken ct = default,
        IAnalyzerProvider? analyzerProvider = null)
    {
        var provider = analyzerProvider ?? _registry;
        var compilation = await session.GetCompilationAsync(ct);

        // Select analyzers based on filters
        var analyzers = provider.GetAllAnalyzers();
        if (copFilter is not null)
        {
            analyzers = analyzers
                .Where(a => a.SupportedDiagnostics.Any(d => copFilter.Contains(provider.GetCopName(d.Id))))
                .ToImmutableArray();
        }

        if (analyzers.Length == 0)
            return [];

        // Run analyzers
        var compilationWithAnalyzers = new CompilationWithAnalyzers(
            compilation, analyzers, null!, ct);

        var allDiagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

        // Apply filters
        var results = new List<DiagnosticResult>();
        foreach (var diagnostic in allDiagnostics)
        {
            if (ct.IsCancellationRequested)
                break;

            // Ruleset filter: suppress diagnostics set to None, override severity for others
            var effectiveSeverity = diagnostic.Severity;
            if (provider is AnalyzerSet analyzerSet && analyzerSet.RuleActions is { } ruleActions)
            {
                if (ruleActions.TryGetValue(diagnostic.Id, out var action))
                {
                    if (action == RuleAction.None)
                        continue;
                    if (action != RuleAction.Default)
                        effectiveSeverity = MapRuleActionToSeverity(action, diagnostic.Severity);
                }
                else if (ruleActions.TryGetValue("*", out var generalAction) && generalAction == RuleAction.None)
                {
                    continue;
                }
            }

            // Severity filter (applied after ruleset overrides)
            if (minSeverity.HasValue && effectiveSeverity < minSeverity.Value)
                continue;

            // Diagnostic ID filter
            if (diagnosticIdFilter is not null && !diagnosticIdFilter.Contains(diagnostic.Id))
                continue;

            // File path filter
            var location = diagnostic.Location;
            var diagnosticFilePath = location.SourceTree?.FilePath;
            if (filePath is not null && diagnosticFilePath is not null)
            {
                if (!Path.GetFullPath(diagnosticFilePath).Equals(Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            // Cop name filter (applied per-diagnostic for precision)
            var copName = provider.GetCopName(diagnostic.Id);
            if (copFilter is not null && !copFilter.Contains(copName))
                continue;

            results.Add(MapDiagnostic(diagnostic, copName, provider, effectiveSeverity));
        }

        return results;
    }

    private static DiagnosticSeverity MapRuleActionToSeverity(RuleAction action, DiagnosticSeverity fallback) => action switch
    {
        RuleAction.Error => DiagnosticSeverity.Error,
        RuleAction.Warning => DiagnosticSeverity.Warning,
        RuleAction.Info => DiagnosticSeverity.Info,
        RuleAction.Hidden => DiagnosticSeverity.Hidden,
        _ => fallback
    };

    private static DiagnosticResult MapDiagnostic(Diagnostic diagnostic, string copName, IAnalyzerProvider provider, DiagnosticSeverity effectiveSeverity)
    {
        var location = diagnostic.Location;
        var lineSpan = location.GetLineSpan();

        return new DiagnosticResult(
            Id: diagnostic.Id,
            Message: diagnostic.GetMessage(),
            Severity: effectiveSeverity.ToString(),
            FilePath: location.SourceTree?.FilePath ?? "",
            StartLine: lineSpan.StartLinePosition.Line + 1,
            StartColumn: lineSpan.StartLinePosition.Character + 1,
            EndLine: lineSpan.EndLinePosition.Line + 1,
            EndColumn: lineSpan.EndLinePosition.Character + 1,
            CopName: copName,
            HasCodeFix: provider.HasCodeFix(diagnostic.Id));
    }
}
