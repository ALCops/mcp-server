using System.ComponentModel;
using System.Text.Json;
using ALCops.Mcp.Services;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Tools;

[McpServerToolType]
public sealed class AnalyzeTool
{
    [McpServerTool(Name = "analyze", ReadOnly = true),
     Description("Run ALCops analyzers on an AL project or a specific file. Returns diagnostics with location info, severity, and whether a code fix is available.")]
    public static async Task<string> Analyze(
        ProjectSessionManager sessionManager,
        DiagnosticsRunner runner,
        ProjectAnalyzerResolver analyzerResolver,
        [Description("Absolute path to the AL project folder (must contain app.json).")] string projectPath,
        [Description("Optional: absolute path to a specific .al file to analyze. If omitted, analyzes the entire project.")] string? filePath = null,
        [Description("Optional: comma-separated cop names to include (e.g., 'LinterCop,ApplicationCop'). If omitted, all cops run.")] string? copFilter = null,
        [Description("Optional: minimum severity to include (Hidden, Info, Warning, Error). Default: Info.")] string? minSeverity = null,
        [Description("Optional: JSON array of analyzer specs (e.g., '[\"${CodeCop}\",\"${UICop}\"]'). If omitted, auto-discovers from .vscode/settings.json. ALCops analyzers are always included.")] string? analyzers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await sessionManager.GetOrLoadProjectAsync(projectPath, cancellationToken);

            IReadOnlySet<string>? copSet = null;
            if (copFilter is not null)
            {
                copSet = copFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics.DiagnosticSeverity? severity = null;
            if (minSeverity is not null)
            {
                severity = minSeverity.ToLowerInvariant() switch
                {
                    "hidden" => Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics.DiagnosticSeverity.Hidden,
                    "info" => Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics.DiagnosticSeverity.Info,
                    "warning" => Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics.DiagnosticSeverity.Warning,
                    "error" => Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics.DiagnosticSeverity.Error,
                    _ => null
                };
            }

            var analyzerSpecs = ParseAnalyzerSpecs(analyzers);
            var analyzerSet = await analyzerResolver.ResolveAsync(projectPath, analyzerSpecs, cancellationToken);

            var diagnostics = await runner.RunAsync(
                session, filePath, copSet, diagnosticIdFilter: null, severity, cancellationToken,
                analyzerProvider: analyzerSet);

            var result = new { diagnostics, warnings = analyzerSet.Warnings.Count > 0 ? analyzerSet.Warnings : null };
            return JsonSerializer.Serialize(result, JsonDefaults.Options);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.GetType().Name, message = ex.Message }, JsonDefaults.Options);
        }
    }

    private static IReadOnlyList<string>? ParseAnalyzerSpecs(string? analyzers)
    {
        if (analyzers is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<string>>(analyzers);
        }
        catch
        {
            return null;
        }
    }
}
