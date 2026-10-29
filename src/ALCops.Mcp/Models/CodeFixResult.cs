namespace ALCops.Mcp.Models;

/// <summary>
/// A computed (not yet written) code fix: the action's title, the equivalence key that was applied,
/// and every file it changes, ordered by path. <see cref="Changes"/> is never empty.
/// </summary>
public record CodeFixResult(
    string FixTitle,
    string EquivalenceKey,
    IReadOnlyList<DocumentChange> Changes);
