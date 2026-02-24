using System.ComponentModel;
using System.Text.Json;
using ALCops.Mcp.Services;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Tools;

[McpServerToolType]
public sealed class GetFixesTool
{
    [McpServerTool(Name = "get_fixes", ReadOnly = true),
     Description("Get available code fixes for a specific diagnostic at a location. Returns fix titles and equivalence keys needed by apply_fix.")]
    public static async Task<string> GetFixes(
        ProjectSessionManager sessionManager,
        CodeFixRunner codeFixRunner,
        ProjectAnalyzerResolver analyzerResolver,
        [Description("Absolute path to the AL project folder (must contain app.json).")] string projectPath,
        [Description("Absolute path to the .al file containing the diagnostic.")] string filePath,
        [Description("The diagnostic rule ID (e.g., 'AC0018', 'LC0001').")] string diagnosticId,
        [Description("Line number of the diagnostic (1-based).")] int line,
        [Description("Column number of the diagnostic (1-based).")] int column,
        [Description("Optional: JSON array of analyzer specs (e.g., '[\"${CodeCop}\",\"${UICop}\"]'). If omitted, auto-discovers from .vscode/settings.json. ALCops analyzers are always included.")] string? analyzers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await sessionManager.GetOrLoadProjectAsync(projectPath, cancellationToken);

            var analyzerSpecs = ParseAnalyzerSpecs(analyzers);
            var analyzerSet = await analyzerResolver.ResolveAsync(projectPath, analyzerSpecs, cancellationToken);

            var fixes = await codeFixRunner.GetFixesAsync(
                session, filePath, diagnosticId, line, column, cancellationToken,
                analyzerProvider: analyzerSet);

            return JsonSerializer.Serialize(fixes, JsonDefaults.Options);
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
