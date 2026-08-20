using System.ComponentModel;
using System.Text.Json;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Tools;

[McpServerToolType]
public sealed class ApplyFixAllTool
{
    [McpServerTool(Name = "apply_fix_all", ReadOnly = false, Destructive = false),
     Description("Apply a code fix to every occurrence of a diagnostic rule across a project (or a single file). " +
        "Runs analysis once, then fixes all matches for that rule ID in one pass — like VS Code's 'Fix all in workspace'. " +
        "Writes changed files directly to disk unless dryRun is true. Use get_fixes first to discover equivalenceKey options.")]
    public static async Task<string> ApplyFixAll(
        ProjectSessionManager sessionManager,
        CodeFixRunner codeFixRunner,
        ProjectAnalyzerResolver analyzerResolver,
        [Description("Absolute path to the AL project folder (must contain app.json).")] string projectPath,
        [Description("The diagnostic rule ID to fix everywhere (e.g., 'AC0018', 'LC0001'). Exactly one rule per call.")] string diagnosticId,
        [Description("Fix scope: 'project' (default, every file in the project) or 'document' (a single file, requires filePath). " +
            "There is no separate 'workspace' scope — each loaded AL project is already the whole workspace.")] string scope = "project",
        [Description("Absolute path to a single .al file. Required when scope='document'; ignored (with a warning) when scope='project'.")] string? filePath = null,
        [Description("Equivalence key of the fix to apply (from get_fixes results). Required only if the rule offers more than one distinct fix; omit otherwise.")] string? equivalenceKey = null,
        [Description("Optional: JSON array of analyzer specs (e.g., '[\"${CodeCop}\",\"${UICop}\"]'). If omitted, auto-discovers from .vscode/settings.json. ALCops analyzers are always included.")] string? analyzers = null,
        [Description("If true, computes and reports the changes without writing to disk.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FixAllScope fixAllScope;
            if (string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase))
                fixAllScope = FixAllScope.Project;
            else if (string.Equals(scope, "document", StringComparison.OrdinalIgnoreCase))
                fixAllScope = FixAllScope.Document;
            else
                return JsonSerializer.Serialize(
                    new { error = "InvalidScope", message = $"Unknown scope '{scope}'. Use 'project' or 'document'." },
                    JsonDefaults.Options);

            if (fixAllScope == FixAllScope.Document && string.IsNullOrWhiteSpace(filePath))
                return JsonSerializer.Serialize(
                    new { error = "MissingFilePath", message = "filePath is required when scope='document'." },
                    JsonDefaults.Options);

            string? warning = null;
            if (fixAllScope == FixAllScope.Project && !string.IsNullOrWhiteSpace(filePath))
            {
                warning = "filePath is ignored when scope='project'; the fix was applied across the whole project.";
                filePath = null;
            }

            var session = await sessionManager.GetOrLoadProjectAsync(projectPath, cancellationToken);

            var analyzerSpecs = ParseAnalyzerSpecs(analyzers);
            var analyzerSet = await analyzerResolver.ResolveAsync(projectPath, analyzerSpecs, cancellationToken);

            var result = await codeFixRunner.ApplyFixAllAsync(
                session, diagnosticId, fixAllScope, filePath, equivalenceKey, cancellationToken,
                analyzerProvider: analyzerSet);

            switch (result.Status)
            {
                case FixAllStatus.NoDiagnosticsFound:
                    return JsonSerializer.Serialize(new
                    {
                        applied = false,
                        dryRun,
                        diagnosticId,
                        diagnosticsFound = 0,
                        message = $"No occurrences of {diagnosticId} were found in the given scope.",
                        warning
                    }, JsonDefaults.Options);

                case FixAllStatus.NoFixAvailable:
                    return JsonSerializer.Serialize(new
                    {
                        error = "NoFixAvailable",
                        message = $"No applicable code fix found for {diagnosticId}" +
                            (equivalenceKey is not null ? $" with equivalence key '{equivalenceKey}'." : "."),
                        diagnosticsFound = result.DiagnosticsFound
                    }, JsonDefaults.Options);

                case FixAllStatus.AmbiguousFix:
                    return JsonSerializer.Serialize(new
                    {
                        error = "AmbiguousFix",
                        message = $"{diagnosticId} has multiple distinct fixes available. " +
                            "Call get_fixes on one occurrence to see titles, then pass the desired equivalenceKey.",
                        diagnosticsFound = result.DiagnosticsFound,
                        availableEquivalenceKeys = result.AvailableEquivalenceKeys
                    }, JsonDefaults.Options);
            }

            // Completed
            if (!dryRun)
            {
                foreach (var change in result.Changes)
                    await File.WriteAllTextAsync(change.FilePath, change.ModifiedContent, cancellationToken);

                if (result.Changes.Count > 0)
                    await sessionManager.ReloadProjectAsync(projectPath, cancellationToken);
            }

            return JsonSerializer.Serialize(new
            {
                applied = !dryRun && result.Changes.Count > 0,
                dryRun,
                diagnosticId,
                fixTitle = result.FixTitle,
                equivalenceKey = result.EquivalenceKey,
                diagnosticsFound = result.DiagnosticsFound,
                filesChanged = result.Changes.Select(c => c.FilePath).ToArray(),
                unfixedDiagnostics = result.Unfixed,
                warning
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
