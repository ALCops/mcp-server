namespace ALCops.Mcp.Models;

/// <summary>
/// Coarse-grained outcome of an apply_fix_all request. <see cref="FixAllResult.Changes"/> and
/// <see cref="FixAllResult.Unfixed"/> carry the fine-grained detail for the <see cref="Completed"/> case.
/// </summary>
public enum FixAllStatus
{
    /// <summary>The fix-all pass ran; see Changes/Unfixed for what happened (possibly zero changes).</summary>
    Completed,
    NoDiagnosticsFound,
    /// <summary>No fix could be chosen; <see cref="FixAllResult.NotFoundReason"/> says why.</summary>
    NotFound,
    /// <summary>Several distinct fixes apply and no equivalence key was given; see <see cref="FixAllResult.Candidates"/>.</summary>
    Ambiguous
}

/// <summary>One file whose content changed (or would change, if dryRun) as a result of the fix-all pass.</summary>
public record FixAllFileChange(string FilePath, string OriginalContent, string ModifiedContent, IReadOnlyList<FixAllUnfixedDiagnostic> Diagnostics);

/// <summary>A diagnostic matching the requested rule that no available fix could resolve.</summary>
public record FixAllUnfixedDiagnostic(string FilePath, int Line, int Column);

/// <param name="Candidates">
/// The distinct fixes offered for the probe diagnostic, filled for <see cref="FixAllStatus.Ambiguous"/>
/// and for <see cref="FixNotFoundReason.NoFixForEquivalenceKey"/>; empty otherwise.
/// </param>
/// <param name="NotFoundReason">Set when <see cref="Status"/> is <see cref="FixAllStatus.NotFound"/>.</param>
public record FixAllResult(
    FixAllStatus Status,
    string DiagnosticId,
    int DiagnosticsFound,
    string? FixTitle,
    string? EquivalenceKey,
    IReadOnlyList<FixAllFileChange> Changes,
    IReadOnlyList<CodeFixInfo> Candidates,
    IReadOnlyList<FixAllUnfixedDiagnostic> Unfixed,
    FixNotFoundReason? NotFoundReason = null);
