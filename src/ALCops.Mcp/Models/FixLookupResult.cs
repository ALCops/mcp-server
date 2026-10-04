using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;

namespace ALCops.Mcp.Models;

/// <summary>
/// Why a fix lookup found nothing. Serialized by name as the <c>reason</c> of a <c>NotFound</c> error,
/// so the member names are part of the tool contract.
/// </summary>
public enum FixNotFoundReason
{
    /// <summary>No loaded analyzer assembly registers a code fix provider for the rule.</summary>
    NoFixProvider,
    /// <summary>The file is not a document of the project.</summary>
    FileNotInProject,
    /// <summary>A fix provider exists, but no loaded analyzer reports the rule.</summary>
    NoAnalyzerForRule,
    /// <summary>The project's ruleset sets the rule to <c>None</c>.</summary>
    SuppressedByRuleset,
    /// <summary>The diagnostic exists at the position but a <c>#pragma warning disable</c> suppresses it.</summary>
    SuppressedByPragma,
    /// <summary>The rule is not reported at that line/column (nor anywhere on that line).</summary>
    NoDiagnosticAtPosition,
    /// <summary>The diagnostic was found, but no provider registered an applicable fix for it.</summary>
    NoFixForDiagnostic,
    /// <summary>Fixes exist, but none has the requested equivalence key.</summary>
    NoFixForEquivalenceKey,
}

/// <summary>Outcome of <c>CodeFixRunner.GetFixesAsync</c>: either fixes, or the reason there are none.</summary>
public record FixLookupResult(IReadOnlyList<CodeFixInfo> Fixes, FixNotFoundReason? NotFoundReason)
{
    public static FixLookupResult Found(IReadOnlyList<CodeFixInfo> fixes) => new(fixes, null);
    public static FixLookupResult NotFound(FixNotFoundReason reason) => new([], reason);
}

/// <summary>
/// Outcome of <c>CodeFixRunner.ApplyFixAsync</c>: the computed fix, or the reason there is none.
/// <see cref="Candidates"/> is filled for <see cref="FixNotFoundReason.NoFixForEquivalenceKey"/>.
/// </summary>
public record FixApplyResult(CodeFixResult? Fix, FixNotFoundReason? NotFoundReason, IReadOnlyList<CodeFixInfo> Candidates)
{
    public static FixApplyResult Applied(CodeFixResult fix) => new(fix, null, []);

    public static FixApplyResult NotFound(FixNotFoundReason reason, IReadOnlyList<CodeFixInfo>? candidates = null) =>
        new(null, reason, candidates ?? []);
}

/// <summary>A diagnostic found at a position, or the reason none was.</summary>
internal readonly record struct DiagnosticLookup(Diagnostic? Diagnostic, FixNotFoundReason? Reason);
