using System.ComponentModel;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Tools;

[McpServerToolType]
public sealed class ApplyFixAllTool
{
    [McpServerTool(Name = "apply_fix_all", ReadOnly = false, Destructive = false),
     Description("Apply a code fix to every occurrence of a diagnostic rule across a project (or a single file). " +
        "Runs analysis once, then fixes all matches for that rule ID in one pass — like VS Code's 'Fix all in workspace'. " +
        "Writes changed files directly to disk unless dryRun is true. " +
        "If the rule offers more than one distinct fix and no equivalenceKey is given, returns the error Ambiguous whose candidates " +
        "list each fix's equivalenceKey, title and providerName; pass one equivalenceKey verbatim. " +
        "Zero occurrences is a success (applied: false, diagnosticsFound: 0), except a rule the project ruleset suppresses, which is NotFound with reason SuppressedByRuleset. " +
        "Changed project files are re-read from disk first. Files that change on disk while the fix is being computed " +
        "(or that cannot be read, or are not valid in their detected encoding) " +
        "are left untouched and listed in 'conflicts' (each with a 'kind') and their diagnostics remain in 'unfixedDiagnostics' (positions as analysed, so they may have shifted if the file was edited); the other files are still written. " +
        "If a write fails, every file written in this call is restored (unless it was edited since) and all of them are listed in 'conflicts'. " +
        "Verify with analyze or al_compile (options.onlyErrors: false).")]
    public static async Task<CallToolResult> ApplyFixAll(
        ProjectSessionManager sessionManager,
        CodeFixRunner codeFixRunner,
        ProjectAnalyzerResolver analyzerResolver,
        GuardedFileWriter fileWriter,
        [Description("Absolute path to the AL project folder (must contain app.json).")] string projectPath,
        [Description("The diagnostic rule ID to fix everywhere (e.g., 'AC0018', 'LC0001'). Exactly one rule per call.")] string diagnosticId,
        [Description("Fix scope: 'project' (default, every file in the project) or 'document' (a single file, requires filePath). " +
            "There is no separate 'workspace' scope — each loaded AL project is already the whole workspace.")] string scope = "project",
        [Description("Absolute path to a single .al file. Required when scope='document'; ignored (with a warning) when scope='project'.")] string? filePath = null,
        [Description("Equivalence key of the fix to apply (from get_fixes results). Required only if the rule offers more than one distinct fix; omit otherwise.")] string? equivalenceKey = null,
        [Description("Optional: JSON array of analyzer specs (e.g., '[\"${CodeCop}\",\"${UICop}\"]'). If omitted, auto-discovers from .vscode/settings.json.")] string? analyzers = null,
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
                return ToolErrors.Invalid($"Unknown scope '{scope}'. Use 'project' or 'document'.");

            if (fixAllScope == FixAllScope.Document && string.IsNullOrWhiteSpace(filePath))
                return ToolErrors.Invalid("filePath is required when scope='document'.");

            if (!ProjectScope.RequireProjectFolder(projectPath, out var invalidMessage))
                return ToolErrors.Invalid(invalidMessage!);

            string? warning = null;
            if (fixAllScope == FixAllScope.Project && !string.IsNullOrWhiteSpace(filePath))
            {
                warning = "filePath is ignored when scope='project'; the fix was applied across the whole project.";
                filePath = null;
            }

            var session = await sessionManager.GetOrLoadProjectAsync(projectPath, cancellationToken);

            var analyzerSpecs = AnalyzerSpec.ParseJsonArray(analyzers);
            var analyzerSet = await analyzerResolver.ResolveAsync(projectPath, analyzerSpecs, cancellationToken);

            var result = await codeFixRunner.ApplyFixAllAsync(
                session, diagnosticId, fixAllScope, filePath, equivalenceKey, analyzerSet, cancellationToken);

            switch (result.Status)
            {
                case FixAllStatus.NoDiagnosticsFound:
                    return ToolResults.Ok(new
                    {
                        applied = false,
                        dryRun,
                        diagnosticId,
                        diagnosticsFound = 0,
                        message = $"No occurrences of {diagnosticId} were found in the given scope.",
                        warning
                    });

                case FixAllStatus.NotFound:
                    var reason = result.NotFoundReason!.Value;
                    return ToolErrors.NotFound(reason,
                        ToolErrors.NotFoundMessage(reason, diagnosticId, filePath, equivalenceKey: equivalenceKey),
                        filePath, diagnosticId,
                        reason == FixNotFoundReason.NoFixForEquivalenceKey ? result.Candidates : null);

                case FixAllStatus.Ambiguous:
                    return ToolErrors.Ambiguous(
                        $"{diagnosticId} has {result.Candidates.Count} distinct fixes. Pass one candidate's equivalenceKey verbatim.",
                        result.Candidates, filePath, diagnosticId);
            }

            // Completed
            IReadOnlyList<string> written = [];
            List<FileWriteConflict> conflicts = [];
            string? conflictMessage = null;

            if (!dryRun)
            {
                var batch = await fileWriter.WriteAllIfUnchangedAsync(
                    [.. result.Changes.Select(c => new PendingWrite(c.FilePath, c.OriginalContent, c.ModifiedContent))],
                    cancellationToken);

                written = batch.Written;
                conflicts = [.. batch.StaleConflicts, .. batch.RolledBack];

                foreach (var conflict in conflicts)
                    Console.Error.WriteLine($"Warning: {conflict.Message}");

                var stale = batch.StaleConflicts.Count;
                conflictMessage = (batch.FailureMessage, stale) switch
                {
                    (null, 0) => null,
                    (null, _) => $"{stale} file(s) were skipped (changed on disk, unreadable, or not valid in their detected encoding; see conflicts); their diagnostics are included in unfixedDiagnostics; re-run apply_fix_all to fix them.",
                    (var failure, 0) => failure,
                    (var failure, _) => $"{failure} {stale} other file(s) were skipped (changed on disk, unreadable, or not valid in their detected encoding; see conflicts).",
                };
            }

            var unfixed = MergeUnfixed(result, conflicts);

            return ToolResults.Ok(new
            {
                applied = !dryRun && written.Count > 0,
                dryRun,
                diagnosticId,
                fixTitle = result.FixTitle,
                equivalenceKey = result.EquivalenceKey,
                diagnosticsFound = result.DiagnosticsFound,
                filesChanged = dryRun ? result.Changes.Select(c => c.FilePath).ToArray() : [.. written],
                conflicts,
                message = conflictMessage,
                unfixedDiagnostics = unfixed,
                warning
            });
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

    internal static IReadOnlyList<FixAllUnfixedDiagnostic> MergeUnfixed(
        FixAllResult result, IReadOnlyCollection<FileWriteConflict> conflicts)
    {
        if (conflicts.Count == 0)
            return result.Unfixed;

        var conflictPaths = new HashSet<string>(
            conflicts.Select(c => c.FilePath), StringComparer.OrdinalIgnoreCase);

        // A conflict file's original diagnostics may overlap with Unfixed (e.g. one the iterative
        // fallback could not fix), so dedupe on the record's value equality.
        return result.Unfixed
            .Concat(result.Changes
                .Where(c => conflictPaths.Contains(c.FilePath))
                .SelectMany(c => c.Diagnostics))
            .Distinct()
            .ToList();
    }
}
