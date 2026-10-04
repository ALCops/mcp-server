using System.ComponentModel;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Tools;

[McpServerToolType]
public sealed class GetFixesTool
{
    [McpServerTool(Name = "get_fixes", ReadOnly = true),
     Description("Get available code fixes for a specific diagnostic at a location. " +
        "Returns { diagnosticId, filePath, line, column, fixes: [{ equivalenceKey, title, providerName }] }; " +
        "pass a fix's equivalenceKey verbatim to apply_fix (or apply_fix_all). fixes is never empty. " +
        "When nothing matches, returns the error NotFound with a reason: NoDiagnosticAtPosition (re-run analyze and use its line/column), " +
        "SuppressedByRuleset or SuppressedByPragma (nothing to fix), NoFixProvider, NoAnalyzerForRule, FileNotInProject or NoFixForDiagnostic.")]
    public static async Task<CallToolResult> GetFixes(
        ProjectSessionManager sessionManager,
        CodeFixRunner codeFixRunner,
        ProjectAnalyzerResolver analyzerResolver,
        [Description("Absolute path to the AL project folder (must contain app.json).")] string projectPath,
        [Description("Absolute path to the .al file containing the diagnostic.")] string filePath,
        [Description("The diagnostic rule ID (e.g., 'AC0018', 'LC0001').")] string diagnosticId,
        [Description("Line number of the diagnostic (1-based).")] int line,
        [Description("Column number of the diagnostic (1-based).")] int column,
        [Description("Optional: JSON array of analyzer specs (e.g., '[\"${CodeCop}\",\"${UICop}\"]'). If omitted, auto-discovers from .vscode/settings.json.")] string? analyzers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ProjectScope.RequireProjectFolder(projectPath, out var invalidMessage))
                return ToolErrors.Invalid(invalidMessage!);

            if (!ProjectScope.TryNormalizePath(filePath, trimTrailingSeparator: false, out var fullFilePath, out var invalidFilePath, "filePath"))
                return ToolErrors.Invalid(invalidFilePath!);
            filePath = fullFilePath;

            var session = await sessionManager.GetOrLoadProjectAsync(projectPath, cancellationToken);

            var analyzerSpecs = AnalyzerSpec.ParseJsonArray(analyzers);
            var analyzerSet = await analyzerResolver.ResolveAsync(projectPath, analyzerSpecs, cancellationToken);

            var lookup = await codeFixRunner.GetFixesAsync(
                session, filePath, diagnosticId, line, column, analyzerSet, cancellationToken);

            if (lookup.NotFoundReason is { } reason)
                return ToolErrors.NotFound(reason,
                    ToolErrors.NotFoundMessage(reason, diagnosticId, filePath, line, column),
                    filePath, diagnosticId);

            return ToolResults.Ok(new GetFixesResult(
                diagnosticId, filePath, line, column, lookup.Fixes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolErrors.Faulted(ex);
        }
    }
}
