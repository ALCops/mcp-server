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
    [McpServerTool(Name = "apply_fix_all", Title = ToolDescriptions.ApplyFixAllTitle,
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Apply a code fix to every occurrence of a diagnostic rule across a project (or a single file). " +
        "Runs analysis once, then fixes all matches for that rule ID in one pass — like VS Code's 'Fix all in workspace'. " +
        "Writes changed files directly to disk unless dryRun is true. " +
        "changes lists exactly the files in filesChanged (every computed change on a dry run, the written files otherwise, none after a rolled-back batch, except files the rollback could not restore, which stay in filesChanged), " +
        "each with a hunks-only unified diff (3 context lines; diff cut at 200 KB (200 × 1024 characters) per file with truncated: true); conflicts carry no diff. " +
        "If the rule offers more than one distinct fix and no equivalenceKey is given, returns the error Ambiguous whose candidates " +
        "list each fix's equivalenceKey, title and providerName; pass one equivalenceKey verbatim. " +
        "Zero occurrences is a success (applied: false, diagnosticsFound: 0), except a rule the project ruleset suppresses (NotFound with reason SuppressedByRuleset) or one no loaded analyzer reports (NotFound with reason NoAnalyzerForRule). " +
        "Every file the fix changes is written, including side edits to files without an occurrence. A fix that adds, removes or renames files, or edits a file outside the project, is NotFound with reason UnsupportedFix and nothing is written. " +
        "Changed project files are re-read from disk first. Files that change on disk while the fix is being computed " +
        "(or that cannot be read, or are not valid in their detected encoding) " +
        "are left untouched and listed in 'conflicts' (each with a 'kind') and their diagnostics remain in 'unfixedDiagnostics' (positions as analysed, so they may have shifted if the file was edited); the other files are still written. " +
        "A fix-all whose changes include side edits to files without an occurrence is written all or none: if any file has a phase-1 conflict, nothing is written. " +
        "If a write fails, every file written in this call is restored (unless it was edited since) and all of them are listed in 'conflicts'. " +
        ToolDescriptions.VerifyAfterWrite)]
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
        [Description("If true, computes and reports the changes without writing to disk. Default: false.")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        await RunAsync(
            sessionManager, codeFixRunner,
            async (path, specs, ct) => await analyzerResolver.ResolveAsync(path, specs, ct),
            fileWriter, projectPath, diagnosticId, scope, filePath, equivalenceKey, analyzers, dryRun, cancellationToken);

    /// <summary>
    /// The body of <see cref="ApplyFixAll"/>, with analyzer resolution passed in so tests can substitute
    /// an <see cref="IAnalyzerProvider"/> (e.g. one with test-only fix providers).
    /// </summary>
    internal static async Task<CallToolResult> RunAsync(
        ProjectSessionManager sessionManager,
        CodeFixRunner codeFixRunner,
        Func<string, IReadOnlyList<string>?, CancellationToken, Task<IAnalyzerProvider>> resolveAnalyzers,
        GuardedFileWriter fileWriter,
        string projectPath,
        string diagnosticId,
        string scope,
        string? filePath,
        string? equivalenceKey,
        string? analyzers,
        bool dryRun,
        CancellationToken cancellationToken)
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

            if (fixAllScope == FixAllScope.Document)
            {
                if (!ProjectScope.TryNormalizePath(filePath!, trimTrailingSeparator: false, out var fullFilePath, out var invalidFilePath, "filePath"))
                    return ToolErrors.Invalid(invalidFilePath!);
                filePath = fullFilePath;
            }

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
            var analyzerSet = await resolveAnalyzers(projectPath, analyzerSpecs, cancellationToken);

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
            var nothingWritten = false;

            if (!dryRun)
            {
                // A change without diagnostics of its own is a side edit. Skipping a stale side-edit file
                // while writing the file whose fix made it would leave the project half-fixed, and its
                // diagnostics could not be re-reported, so such a fix-all is written all or none.
                var hasSideEdits = result.Changes.Any(c => c.Diagnostics.Count == 0);
                var batch = await fileWriter.WriteAllIfUnchangedAsync(
                    [.. result.Changes.Select(c => new PendingWrite(c.FilePath, c.OriginalContent, c.ModifiedContent))],
                    cancellationToken, allOrNothing: hasSideEdits);

                written = batch.Written;
                conflicts = [.. batch.StaleConflicts, .. batch.RolledBack];

                foreach (var conflict in conflicts)
                    Console.Error.WriteLine($"Warning: {conflict.Message}");

                var stale = batch.StaleConflicts.Count;
                nothingWritten = hasSideEdits && stale > 0;
                conflictMessage = nothingWritten
                    ? $"The fix edits other files than the ones with occurrences of {diagnosticId}, and {stale} file(s) " +
                      "changed on disk, could not be read, or are not valid in their detected encoding (see conflicts); " +
                      "nothing was written. Re-run apply_fix_all."
                    : (batch.FailureMessage, stale) switch
                    {
                        (null, 0) => null,
                        (null, _) => $"{stale} file(s) were skipped (changed on disk, unreadable, or not valid in their detected encoding; see conflicts); their diagnostics are included in unfixedDiagnostics; re-run apply_fix_all to fix them.",
                        (var failure, 0) => failure,
                        (var failure, _) => $"{failure} {stale} other file(s) were skipped (changed on disk, unreadable, or not valid in their detected encoding; see conflicts).",
                    };
            }

            // A dry run reports what would remain after the fix; a real run reports what remains on disk,
            // which for every change that was not written (skipped, refused or rolled back) is its
            // original diagnostics, not the post-fix residuals.
            var unfixed = dryRun ? result.Unfixed : MergeUnfixed(result, written);

            // changes describes exactly filesChanged: every computed change on a dry run, else the written files.
            var changedFiles = dryRun
                ? result.Changes
                : [.. result.Changes.Where(c => written.Contains(c.FilePath, StringComparer.OrdinalIgnoreCase))];

            return ToolResults.Ok(new
            {
                applied = !dryRun && written.Count > 0,
                dryRun,
                diagnosticId,
                fixTitle = result.FixTitle,
                equivalenceKey = result.EquivalenceKey,
                diagnosticsFound = result.DiagnosticsFound,
                filesChanged = changedFiles.Select(c => c.FilePath).ToArray(),
                changes = changedFiles.Select(c => UnifiedDiff.Report(c.FilePath, c.OriginalContent, c.ModifiedContent)).ToArray(),
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

    /// <summary>
    /// The unfixed diagnostics of a real run. <see cref="FixAllResult.Unfixed"/> is in post-fix
    /// coordinates, which only exist on disk for files that were written (<paramref name="writtenPaths"/>)
    /// or not changed at all. For every change that was not written (skipped as a conflict, refused
    /// all-or-nothing, or rolled back) its residuals are dropped and its original diagnostics are
    /// reported instead. Paths compare case-insensitively; duplicates (a residual the fallback could
    /// not fix that is also an original) are listed once.
    /// </summary>
    internal static IReadOnlyList<FixAllUnfixedDiagnostic> MergeUnfixed(
        FixAllResult result, IReadOnlyCollection<string> writtenPaths)
    {
        var written = new HashSet<string>(writtenPaths, StringComparer.OrdinalIgnoreCase);
        var unwritten = result.Changes.Where(c => !written.Contains(c.FilePath)).ToList();
        if (unwritten.Count == 0)
            return result.Unfixed;

        var unwrittenPaths = new HashSet<string>(unwritten.Select(c => c.FilePath), StringComparer.OrdinalIgnoreCase);
        return result.Unfixed
            .Where(d => !unwrittenPaths.Contains(d.FilePath))
            .Concat(unwritten.SelectMany(c => c.Diagnostics))
            .Distinct()
            .ToList();
    }
}
