namespace ALCops.Mcp.Models;

/// <summary>Success result of <c>get_fixes</c>. <see cref="Fixes"/> is never empty.</summary>
public record GetFixesResult(
    string DiagnosticId,
    string FilePath,
    int Line,
    int Column,
    IReadOnlyList<CodeFixInfo> Fixes);
