using System.ComponentModel;
using System.Text.Json;
using ALCops.Mcp.Services;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Tools;

[McpServerToolType]
public sealed class ApplyFixTool
{
    [McpServerTool(Name = "apply_fix", ReadOnly = false, Destructive = false),
     Description("Apply a code fix to resolve a diagnostic. Writes the fixed content directly to the file on disk and returns a summary of what changed.")]
    public static async Task<string> ApplyFix(
        ProjectSessionManager sessionManager,
        CodeFixRunner codeFixRunner,
        ProjectAnalyzerResolver analyzerResolver,
        [Description("Absolute path to the AL project folder (must contain app.json).")] string projectPath,
        [Description("Absolute path to the .al file containing the diagnostic.")] string filePath,
        [Description("The diagnostic rule ID (e.g., 'AC0018', 'LC0001').")] string diagnosticId,
        [Description("Line number of the diagnostic (1-based).")] int line,
        [Description("Column number of the diagnostic (1-based).")] int column,
        [Description("Equivalence key of the fix to apply (from get_fixes results).")] string equivalenceKey,
        [Description("Optional: JSON array of analyzer specs (e.g., '[\"${CodeCop}\",\"${UICop}\"]'). If omitted, auto-discovers from .vscode/settings.json. ALCops analyzers are always included.")] string? analyzers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await sessionManager.GetOrLoadProjectAsync(projectPath, cancellationToken);

            var analyzerSpecs = ParseAnalyzerSpecs(analyzers);
            var analyzerSet = await analyzerResolver.ResolveAsync(projectPath, analyzerSpecs, cancellationToken);

            var result = await codeFixRunner.ApplyFixAsync(
                session, filePath, diagnosticId, line, column, equivalenceKey, cancellationToken,
                analyzerProvider: analyzerSet);

            if (result is null)
                return JsonSerializer.Serialize(
                    new { error = "NoFixFound", message = $"No code fix with equivalence key '{equivalenceKey}' found for {diagnosticId} at {filePath}:{line}:{column}." },
                    JsonDefaults.Options);

            // Write the fixed content directly to disk
            await File.WriteAllTextAsync(filePath, result.ModifiedContent, cancellationToken);

            // Reload the project session so subsequent calls see the updated file
            await sessionManager.ReloadProjectAsync(projectPath, cancellationToken);

            return JsonSerializer.Serialize(new
            {
                applied = true,
                filePath,
                fixTitle = result.FixTitle,
                diagnosticId
            }, JsonDefaults.Options);
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
