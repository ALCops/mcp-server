namespace ALCops.Mcp.Models;

/// <summary>One file of an <c>apply_fix_all</c> batch: the text the fix was computed from and the fixed text.</summary>
public record PendingWrite(string FilePath, string ExpectedOriginal, string NewContent);

/// <summary>
/// Outcome of <see cref="Services.GuardedFileWriter.WriteAllIfUnchangedAsync"/>.
/// </summary>
/// <param name="Written">Files now on disk with the new content. After a failed commit this holds only the files that were not restored (the rollback failed, or the file was modified after this call wrote it).</param>
/// <param name="StaleConflicts">Files skipped before anything was written because they changed or disappeared on disk, could not be read, or are not valid UTF-8 (see <see cref="FileWriteConflict.Kind"/>).</param>
/// <param name="RolledBack">After a failed commit: every staged file (including the one that failed) that is back to its original bytes.</param>
/// <param name="FailureMessage">Set when a commit failed; describes the failure and any rollback that failed too.</param>
public record BatchWriteResult(
    IReadOnlyList<string> Written,
    IReadOnlyList<FileWriteConflict> StaleConflicts,
    IReadOnlyList<FileWriteConflict> RolledBack,
    string? FailureMessage);
