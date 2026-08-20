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
    NoFixAvailable,
    AmbiguousFix
}

/// <summary>One file whose content changed (or would change, if dryRun) as a result of the fix-all pass.</summary>
public record FixAllFileChange(string FilePath, string ModifiedContent);

/// <summary>A diagnostic matching the requested rule that no available fix could resolve.</summary>
public record FixAllUnfixedDiagnostic(string FilePath, int Line, int Column);

public record FixAllResult(
    FixAllStatus Status,
    string DiagnosticId,
    int DiagnosticsFound,
    string? FixTitle,
    string? EquivalenceKey,
    IReadOnlyList<FixAllFileChange> Changes,
    IReadOnlyList<string> AvailableEquivalenceKeys,
    IReadOnlyList<FixAllUnfixedDiagnostic> Unfixed);
