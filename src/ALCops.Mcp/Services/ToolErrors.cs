using System.Text.Json;
using ALCops.Mcp.Models;
using ModelContextProtocol.Protocol;

namespace ALCops.Mcp.Services;

/// <summary>
/// The only place a native tool error is built. Every result has <see cref="CallToolResult.IsError"/>
/// set and carries a <see cref="ToolError"/> envelope as its single text block, so a client that hides
/// error results still has the full message.
/// </summary>
internal static class ToolErrors
{
    /// <summary>The arguments are unusable as given.</summary>
    internal static CallToolResult Invalid(string message) =>
        Build(new ToolError(ToolErrorCode.Invalid, message));

    /// <summary>Nothing matched; <paramref name="reason"/> is written by name.</summary>
    internal static CallToolResult NotFound(
        FixNotFoundReason reason,
        string message,
        string? filePath = null,
        string? diagnosticId = null,
        IReadOnlyList<CodeFixInfo>? candidates = null) =>
        Build(new ToolError(ToolErrorCode.NotFound, message, reason.ToString(), candidates, filePath, diagnosticId));

    /// <summary>Several distinct fixes apply; the caller has to pick one of <paramref name="candidates"/>.</summary>
    internal static CallToolResult Ambiguous(
        string message,
        IReadOnlyList<CodeFixInfo> candidates,
        string? filePath = null,
        string? diagnosticId = null) =>
        Build(new ToolError(ToolErrorCode.Ambiguous, message, Candidates: candidates, FilePath: filePath, DiagnosticId: diagnosticId));

    /// <summary>The file changed or was deleted after the fix was computed; nothing was written.</summary>
    internal static CallToolResult Stale(string filePath, string? diagnosticId, string message) =>
        Build(new ToolError(ToolErrorCode.Stale, message, FilePath: filePath, DiagnosticId: diagnosticId));

    /// <summary>A dependency is down; <paramref name="reason"/> is an <see cref="UnavailableReason"/> value.</summary>
    internal static CallToolResult Unavailable(string reason, string message, string? detail = null) =>
        Build(new ToolError(ToolErrorCode.Unavailable, message, reason, Detail: detail));

    /// <summary>An unexpected exception: <c>message</c> is its message, <c>detail</c> its full type name.</summary>
    internal static CallToolResult Faulted(Exception ex) =>
        Build(new ToolError(ToolErrorCode.Faulted, ex.Message, Detail: ex.GetType().FullName));

    /// <summary>A refused or failed write; <paramref name="reason"/> is the <see cref="FileWriteConflictKind"/>.</summary>
    internal static CallToolResult Faulted(
        string reason,
        string message,
        string? filePath = null,
        string? diagnosticId = null,
        string? detail = null) =>
        Build(new ToolError(ToolErrorCode.Faulted, message, reason, FilePath: filePath, DiagnosticId: diagnosticId, Detail: detail));

    /// <summary>
    /// The shared wording of a <see cref="ToolErrorCode.NotFound"/> message, so the three fix tools
    /// say the same thing for the same reason. <paramref name="line"/>/<paramref name="column"/> are
    /// null for apply_fix_all, which has no position.
    /// </summary>
    internal static string NotFoundMessage(
        FixNotFoundReason reason,
        string diagnosticId,
        string? filePath,
        int? line = null,
        int? column = null,
        string? equivalenceKey = null)
    {
        var at = line is null
            ? (filePath is null ? "in the project" : $"in {filePath}")
            : $"at {filePath}:{line}:{column}";

        return reason switch
        {
            FixNotFoundReason.NoFixProvider =>
                $"No code fix provider is registered for {diagnosticId} by the configured analyzers.",
            FixNotFoundReason.FileNotInProject =>
                $"{filePath} is not an .al file of this project.",
            FixNotFoundReason.NoAnalyzerForRule =>
                $"No loaded analyzer reports {diagnosticId}, so the diagnostic cannot be located. Check al.codeAnalyzers.",
            FixNotFoundReason.SuppressedByRuleset =>
                $"{diagnosticId} is suppressed by the project ruleset (action None); there is nothing to fix.",
            FixNotFoundReason.SuppressedByPragma =>
                $"{diagnosticId} {at} is suppressed by #pragma warning disable; there is nothing to fix.",
            FixNotFoundReason.NoDiagnosticAtPosition =>
                $"No {diagnosticId} {at}; re-run analyze and use its line/column.",
            FixNotFoundReason.NoFixForDiagnostic =>
                $"{diagnosticId} {at} was found, but no code fix applies to it.",
            FixNotFoundReason.NoFixForEquivalenceKey =>
                $"No fix for {diagnosticId} {at} has equivalence key '{equivalenceKey}'. Pass one candidate's equivalenceKey verbatim.",
            _ => $"No fix found for {diagnosticId} {at}.",
        };
    }

    private static CallToolResult Build(ToolError error) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(error, JsonDefaults.Options) }]
    };
}
