using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;

namespace ALCops.Mcp.Services;

/// <summary>
/// Shared ruleset (RuleAction) suppression/override logic, used by CodeFixRunner across
/// `get_fixes`/`apply_fix`/`apply_fix_all`. The child almcp applies the same ruleset to
/// `al_compile` via the `--rulesetpath` the startup bridge hands it, so a rule suppressed by a
/// ruleset is suppressed on both sides.
/// </summary>
public static class RulesetFilter
{
    /// <summary>
    /// Returns true if <paramref name="diagnosticId"/> is suppressed (RuleAction.None) by the given
    /// ruleset, either directly or via the "*" wildcard. When not suppressed, <paramref name="severityOverride"/>
    /// carries a non-default severity override (Error/Warning/Info/Hidden) if the ruleset specifies one.
    /// </summary>
    public static bool IsSuppressed(
        Dictionary<string, RuleAction>? ruleActions,
        string diagnosticId,
        out DiagnosticSeverity? severityOverride)
    {
        severityOverride = null;

        if (ruleActions is null)
            return false;

        if (ruleActions.TryGetValue(diagnosticId, out var action))
        {
            if (action == RuleAction.None)
                return true;

            if (action != RuleAction.Default)
                severityOverride = MapToSeverity(action);

            return false;
        }

        return ruleActions.TryGetValue("*", out var generalAction) && generalAction == RuleAction.None;
    }

    public static DiagnosticSeverity MapToSeverity(RuleAction action, DiagnosticSeverity fallback = DiagnosticSeverity.Warning) => action switch
    {
        RuleAction.Error => DiagnosticSeverity.Error,
        RuleAction.Warning => DiagnosticSeverity.Warning,
        RuleAction.Info => DiagnosticSeverity.Info,
        RuleAction.Hidden => DiagnosticSeverity.Hidden,
        _ => fallback
    };
}
