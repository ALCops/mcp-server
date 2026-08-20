using ALCops.Mcp.Services;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// Unit tests for the shared ruleset (RuleAction) suppression/override logic used by
/// DiagnosticsRunner (`analyze`) and CodeFixRunner (`get_fixes`/`apply_fix`/`apply_fix_all`).
/// </summary>
public class RulesetFilterTests
{
    [Fact]
    public void IsSuppressed_NullRuleActions_ReturnsFalseAndNoOverride()
    {
        var suppressed = RulesetFilter.IsSuppressed(null, "LC0020", out var severityOverride);

        Assert.False(suppressed);
        Assert.Null(severityOverride);
    }

    [Fact]
    public void IsSuppressed_RuleSetToNone_ReturnsTrue()
    {
        var ruleActions = new Dictionary<string, RuleAction> { ["LC0020"] = RuleAction.None };

        var suppressed = RulesetFilter.IsSuppressed(ruleActions, "LC0020", out var severityOverride);

        Assert.True(suppressed);
        Assert.Null(severityOverride);
    }

    [Fact]
    public void IsSuppressed_RuleNotMentioned_ReturnsFalse()
    {
        var ruleActions = new Dictionary<string, RuleAction> { ["LC0020"] = RuleAction.None };

        var suppressed = RulesetFilter.IsSuppressed(ruleActions, "AC0012", out var severityOverride);

        Assert.False(suppressed);
        Assert.Null(severityOverride);
    }

    [Fact]
    public void IsSuppressed_WildcardNone_SuppressesUnmentionedRule()
    {
        var ruleActions = new Dictionary<string, RuleAction> { ["*"] = RuleAction.None };

        var suppressed = RulesetFilter.IsSuppressed(ruleActions, "LC0020", out var severityOverride);

        Assert.True(suppressed);
        Assert.Null(severityOverride);
    }

    [Fact]
    public void IsSuppressed_SpecificRuleOverridesWildcard()
    {
        // Wildcard says suppress everything, but LC0020 is explicitly re-enabled as Warning.
        var ruleActions = new Dictionary<string, RuleAction>
        {
            ["*"] = RuleAction.None,
            ["LC0020"] = RuleAction.Warning
        };

        var suppressed = RulesetFilter.IsSuppressed(ruleActions, "LC0020", out var severityOverride);

        Assert.False(suppressed);
        Assert.Equal(DiagnosticSeverity.Warning, severityOverride);
    }

    [Theory]
    [InlineData(RuleAction.Error, DiagnosticSeverity.Error)]
    [InlineData(RuleAction.Warning, DiagnosticSeverity.Warning)]
    [InlineData(RuleAction.Info, DiagnosticSeverity.Info)]
    [InlineData(RuleAction.Hidden, DiagnosticSeverity.Hidden)]
    public void IsSuppressed_NonDefaultAction_ReturnsSeverityOverride(RuleAction action, DiagnosticSeverity expected)
    {
        var ruleActions = new Dictionary<string, RuleAction> { ["LC0020"] = action };

        var suppressed = RulesetFilter.IsSuppressed(ruleActions, "LC0020", out var severityOverride);

        Assert.False(suppressed);
        Assert.Equal(expected, severityOverride);
    }

    [Fact]
    public void IsSuppressed_DefaultAction_ReturnsNoOverride()
    {
        var ruleActions = new Dictionary<string, RuleAction> { ["LC0020"] = RuleAction.Default };

        var suppressed = RulesetFilter.IsSuppressed(ruleActions, "LC0020", out var severityOverride);

        Assert.False(suppressed);
        Assert.Null(severityOverride);
    }

    [Theory]
    [InlineData(RuleAction.Error, DiagnosticSeverity.Error)]
    [InlineData(RuleAction.Warning, DiagnosticSeverity.Warning)]
    [InlineData(RuleAction.Info, DiagnosticSeverity.Info)]
    [InlineData(RuleAction.Hidden, DiagnosticSeverity.Hidden)]
    [InlineData(RuleAction.Default, DiagnosticSeverity.Warning)]
    [InlineData(RuleAction.None, DiagnosticSeverity.Warning)]
    public void MapToSeverity_MapsEachRuleAction(RuleAction action, DiagnosticSeverity expected)
    {
        Assert.Equal(expected, RulesetFilter.MapToSeverity(action, fallback: DiagnosticSeverity.Warning));
    }
}
