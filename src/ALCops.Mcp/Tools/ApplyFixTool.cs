using System.ComponentModel;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Tools;

[McpServerToolType]
public sealed class ApplyFixTool
{
    [McpServerTool(Name = "apply_fix", Title = ToolDescriptions.ApplyFixTitle,
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Apply a code fix to resolve a diagnostic. Changed project files are re-read from disk first. " +
        "Writes the fixed content to the file on disk unless dryRun is true or the file changed after the fix was computed; " +
        "returns { applied, dryRun, diagnosticId, fixTitle, equivalenceKey, filePath, changes } where equivalenceKey is the key that was applied, filePath is the diagnostic's file (a fix may edit other files only, so read changes, not filePath, to see what changed) and " +
        "changes lists every file the fix changes (or would change, on a dry run) with a hunks-only unified diff (3 context lines; diff cut at 200 KB (200 × 1024 characters) per file with truncated: true). " +
        "If a file changed after the fix was computed, nothing is written and the error Stale is returned (re-run). " +
        "A fix that edits several files writes all of them or none. A fix that adds, removes or renames files, or edits a file outside the project, is NotFound with reason UnsupportedFix and nothing is written. " +
        "A file that is not valid in its detected encoding (e.g. a Windows-1252 file read as UTF-8) is never re-encoded: " +
        "nothing is written and the error Faulted with reason UnsupportedEncoding is returned (reason ReadFailed if the file cannot be read, WriteFailed if the write fails). " +
        "equivalenceKey is optional: without it, the rule's single fix is applied, and if the rule offers more than one distinct fix the error Ambiguous is returned whose candidates " +
        "list each fix's equivalenceKey, title and providerName; pass one equivalenceKey verbatim. " +
        "When no fix matches, returns NotFound with a reason, the same as get_fixes; for NoFixForEquivalenceKey, candidates lists the keys that do apply. " +
        "The write is atomic and preserves the file's encoding and line endings. " +
        ToolDescriptions.VerifyAfterWrite)]
    public static async Task<CallToolResult> ApplyFix(
        ProjectSessionManager sessionManager,
        CodeFixRunner codeFixRunner,
        ProjectAnalyzerResolver analyzerResolver,
        GuardedFileWriter fileWriter,
        [Description("Absolute path to the AL project folder (must contain app.json).")] string projectPath,
        [Description("Absolute path to the .al file containing the diagnostic.")] string filePath,
        [Description("The diagnostic rule ID (e.g., 'AC0018', 'LC0001').")] string diagnosticId,
        [Description("Line number of the diagnostic (1-based).")] int line,
        [Description("Column number of the diagnostic (1-based).")] int column,
        [Description("Optional: equivalence key of the fix to apply (from get_fixes or an Ambiguous error's candidates), verbatim. Needed only when the rule offers several distinct fixes.")] string? equivalenceKey = null,
        [Description("Optional: JSON array of analyzer specs (e.g., '[\"${CodeCop}\",\"${UICop}\"]'). If omitted, auto-discovers from .vscode/settings.json.")] string? analyzers = null,
        [Description("If true, computes and reports the changes without writing to disk. Default: false.")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        await RunAsync(
            sessionManager, codeFixRunner,
            async (path, specs, ct) => await analyzerResolver.ResolveAsync(path, specs, ct),
            fileWriter, projectPath, filePath, diagnosticId, line, column, equivalenceKey, analyzers, dryRun, cancellationToken);

    /// <summary>
    /// The body of <see cref="ApplyFix"/>, with analyzer resolution passed in so tests can substitute
    /// an <see cref="IAnalyzerProvider"/> (e.g. one with test-only fix providers).
    /// </summary>
    internal static async Task<CallToolResult> RunAsync(
        ProjectSessionManager sessionManager,
        CodeFixRunner codeFixRunner,
        Func<string, IReadOnlyList<string>?, CancellationToken, Task<IAnalyzerProvider>> resolveAnalyzers,
        GuardedFileWriter fileWriter,
        string projectPath,
        string filePath,
        string diagnosticId,
        int line,
        int column,
        string? equivalenceKey,
        string? analyzers,
        bool dryRun,
        CancellationToken cancellationToken)
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
            var analyzerSet = await resolveAnalyzers(projectPath, analyzerSpecs, cancellationToken);

            var outcome = await codeFixRunner.ApplyFixAsync(
                session, filePath, diagnosticId, line, column, equivalenceKey, analyzerSet, cancellationToken);

            if (outcome.Status == FixApplyStatus.Ambiguous)
                return ToolErrors.Ambiguous(
                    $"{diagnosticId} has {outcome.Candidates.Count} distinct fixes. Pass one candidate's equivalenceKey verbatim.",
                    outcome.Candidates, filePath, diagnosticId);

            if (outcome.NotFoundReason is { } reason)
                return ToolErrors.NotFound(reason,
                    outcome.MessageOverride
                        ?? ToolErrors.NotFoundMessage(reason, diagnosticId, filePath, line, column, equivalenceKey),
                    filePath, diagnosticId,
                    reason == FixNotFoundReason.NoFixForEquivalenceKey ? outcome.Candidates : null);

            var fix = outcome.Fix!;

            if (!dryRun)
            {
                var writeError = fix.Changes.Count == 1
                    ? await WriteOneAsync(fileWriter, fix.Changes[0], diagnosticId, cancellationToken)
                    : await WriteAllOrNothingAsync(fileWriter, fix.Changes, diagnosticId, cancellationToken);
                if (writeError is not null)
                    return writeError;
            }

            return ToolResults.Ok(new
            {
                applied = !dryRun,
                dryRun,
                diagnosticId,
                fixTitle = fix.FixTitle,
                equivalenceKey = fix.EquivalenceKey,
                filePath,
                changes = fix.Changes.Select(c => UnifiedDiff.Report(c.FilePath, c.OriginalContent, c.ModifiedContent)).ToArray()
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

    /// <summary>The single-file write: <c>Stale</c> on a stale file, <c>Faulted</c> with the kind otherwise.</summary>
    private static async Task<CallToolResult?> WriteOneAsync(
        GuardedFileWriter fileWriter, DocumentChange change, string diagnosticId, CancellationToken cancellationToken)
    {
        FileWriteConflict? conflict;
        try
        {
            conflict = await fileWriter.WriteIfUnchangedAsync(
                change.FilePath, change.OriginalContent, change.ModifiedContent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Warning: {change.FilePath} could not be written: {ex.Message}");
            return ToolErrors.Faulted(FileWriteConflictKind.WriteFailed,
                $"{change.FilePath} could not be written ({ex.GetType().Name}: {ex.Message}); the file is unchanged.",
                change.FilePath, diagnosticId, detail: ex.GetType().FullName);
        }

        if (conflict is null)
            return null;

        Console.Error.WriteLine($"Warning: {conflict.Message}");
        return conflict.Kind == FileWriteConflictKind.StaleFile
            ? ToolErrors.Stale(conflict.FilePath, diagnosticId, conflict.Message)
            : ToolErrors.Faulted(conflict.Kind, conflict.Message, conflict.FilePath, diagnosticId);
    }

    /// <summary>
    /// A fix that edits several files: all of them are written or none. A stale file is <c>Stale</c>
    /// (the message names every stale file); any other phase-1 conflict is <c>Faulted</c> with its kind;
    /// a failed commit, after the rollback, is <c>Faulted</c> / <c>WriteFailed</c> with the writer's message.
    /// </summary>
    private static async Task<CallToolResult?> WriteAllOrNothingAsync(
        GuardedFileWriter fileWriter, IReadOnlyList<DocumentChange> changes, string diagnosticId, CancellationToken cancellationToken)
    {
        var batch = await fileWriter.WriteAllIfUnchangedAsync(
            [.. changes.Select(c => new PendingWrite(c.FilePath, c.OriginalContent, c.ModifiedContent))],
            cancellationToken, allOrNothing: true);

        foreach (var conflict in batch.StaleConflicts.Concat(batch.RolledBack))
            Console.Error.WriteLine($"Warning: {conflict.Message}");

        var stale = batch.StaleConflicts.Where(c => c.Kind == FileWriteConflictKind.StaleFile).ToList();
        if (stale.Count > 0)
            return ToolErrors.Stale(stale[0].FilePath, diagnosticId,
                $"The fix edits {changes.Count} files and {string.Join(", ", stale.Select(c => c.FilePath))} " +
                "changed on disk (or was deleted) after the fix was computed; nothing was written. Re-run apply_fix.");

        if (batch.StaleConflicts.Count > 0)
        {
            var first = batch.StaleConflicts[0];
            return ToolErrors.Faulted(first.Kind,
                $"{first.Message} The fix edits {changes.Count} files; none was written.",
                first.FilePath, diagnosticId);
        }

        if (batch.FailureMessage is not null)
        {
            var failed = batch.RolledBack.FirstOrDefault(c => c.Kind == FileWriteConflictKind.WriteFailed);
            return ToolErrors.Faulted(FileWriteConflictKind.WriteFailed, batch.FailureMessage,
                failed?.FilePath, diagnosticId);
        }

        return null;
    }
}
