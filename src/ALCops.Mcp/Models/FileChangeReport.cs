namespace ALCops.Mcp.Models;

/// <summary>
/// One file in the <c>changes</c> of an apply_fix or apply_fix_all result: a hunks-only unified diff
/// (3 lines of context, no <c>---</c>/<c>+++</c> header), cut at a line boundary at 200 KB
/// (200 × 1024 characters) with <see cref="Truncated"/> set. A truncated diff keeps its original hunk
/// headers, so their counts can exceed the lines shown. Lines are compared without their terminators:
/// an empty <c>diff</c> with the file listed means only line endings changed, unless
/// <see cref="Truncated"/> is set, which means the first changed line alone exceeds the cap.
/// </summary>
public record FileChangeReport(string FilePath, string Diff, bool Truncated);
