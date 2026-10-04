using System.ComponentModel;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Tools;

[McpServerToolType]
public sealed class ApplyFixTool
{
    [McpServerTool(Name = "apply_fix", ReadOnly = false, Destructive = false),
     Description("Apply a code fix to resolve a diagnostic. Changed project files are re-read from disk first. " +
        "Writes the fixed content to the file on disk unless the file changed after the fix was computed, " +
        "in which case nothing is written and the error Stale is returned (re-run). " +
        "A file that is not valid in its detected encoding (e.g. a Windows-1252 file read as UTF-8) is never re-encoded: " +
        "nothing is written and the error Faulted with reason UnsupportedEncoding is returned (reason ReadFailed if the file cannot be read, WriteFailed if the write fails). " +
        "When no fix matches, returns NotFound with a reason, the same as get_fixes; for NoFixForEquivalenceKey, candidates lists the keys that do apply. " +
        "The write is atomic and preserves the file's encoding and line endings. " +
        "Verify with analyze or al_compile (options.onlyErrors: false).")]
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
        [Description("Equivalence key of the fix to apply (from get_fixes results), verbatim.")] string equivalenceKey,
        [Description("Optional: JSON array of analyzer specs (e.g., '[\"${CodeCop}\",\"${UICop}\"]'). If omitted, auto-discovers from .vscode/settings.json.")] string? analyzers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ProjectScope.RequireProjectFolder(projectPath, out var invalidMessage))
                return ToolErrors.Invalid(invalidMessage!);

            var session = await sessionManager.GetOrLoadProjectAsync(projectPath, cancellationToken);

            var analyzerSpecs = AnalyzerSpec.ParseJsonArray(analyzers);
            var analyzerSet = await analyzerResolver.ResolveAsync(projectPath, analyzerSpecs, cancellationToken);

            var outcome = await codeFixRunner.ApplyFixAsync(
                session, filePath, diagnosticId, line, column, equivalenceKey, analyzerSet, cancellationToken);

            if (outcome.NotFoundReason is { } reason)
                return ToolErrors.NotFound(reason,
                    outcome.MessageOverride
                        ?? ToolErrors.NotFoundMessage(reason, diagnosticId, filePath, line, column, equivalenceKey),
                    filePath, diagnosticId,
                    reason == FixNotFoundReason.NoFixForEquivalenceKey ? outcome.Candidates : null);

            var fix = outcome.Fix!;

            FileWriteConflict? conflict;
            try
            {
                conflict = await fileWriter.WriteIfUnchangedAsync(
                    filePath, fix.OriginalContent, fix.ModifiedContent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Warning: {filePath} could not be written: {ex.Message}");
                return ToolErrors.Faulted(FileWriteConflictKind.WriteFailed,
                    $"{filePath} could not be written ({ex.GetType().Name}: {ex.Message}); the file is unchanged.",
                    filePath, diagnosticId, detail: ex.GetType().FullName);
            }

            if (conflict is not null)
            {
                Console.Error.WriteLine($"Warning: {conflict.Message}");
                return conflict.Kind == FileWriteConflictKind.StaleFile
                    ? ToolErrors.Stale(conflict.FilePath, diagnosticId, conflict.Message)
                    : ToolErrors.Faulted(conflict.Kind, conflict.Message, conflict.FilePath, diagnosticId);
            }

            return ToolResults.Ok(new
            {
                applied = true,
                filePath,
                fixTitle = fix.FixTitle,
                diagnosticId
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
}
