using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// The previous parser shared one <c>i + 1 &lt; args.Length</c> guard across every passthrough flag
/// and unconditionally consumed the next arg, so the value-less flags either vanished (when last)
/// or swallowed the following flag as their value. These tests pin the three arities down.
/// </summary>
public class ProxyOptionsTests
{
    [Fact]
    public void Parse_BooleanFlagFollowedByAnotherFlag_DoesNotSwallowIt()
    {
        var options = ProxyOptions.Parse(["--nolog", "--rulesetpath", "custom.ruleset.json"]);

        Assert.Equal(["--nolog", "--rulesetpath", "custom.ruleset.json"], options.PassthroughArgs);
    }

    [Fact]
    public void Parse_TrailingBooleanFlag_IsNotDropped()
    {
        Assert.Equal(["--nolog"], ProxyOptions.Parse(["--nolog"]).PassthroughArgs);
    }

    [Fact]
    public void Parse_OptionalBooleanFlag_ConsumesOnlyLiteralTrueOrFalse()
    {
        Assert.Equal(
            ["--enablecodeanalysis", "true"],
            ProxyOptions.Parse(["--enablecodeanalysis", "true"]).PassthroughArgs);

        // "--noauth --locale da-DK" must not read "--locale" as noauth's value.
        Assert.Equal(
            ["--noauth", "--locale", "da-DK"],
            ProxyOptions.Parse(["--noauth", "--locale", "da-DK"]).PassthroughArgs);
    }

    [Fact]
    public void Parse_ValueFlagWithMissingValue_IsDroppedRatherThanForwardedBare()
    {
        // almcp would ignore a bare --codeanalyzers anyway; dropping it keeps the child args valid.
        Assert.Empty(ProxyOptions.Parse(["--codeanalyzers"]).PassthroughArgs);
    }

    [Fact]
    public void Parse_OurOwnFlags_AreConsumedNotForwarded()
    {
        var options = ProxyOptions.Parse(
            ["--no-proxy", "--devtools-path", @"C:\tools", "--projects", @"C:\a;C:\b", "--nolog"]);

        Assert.True(options.ProxyDisabled);
        Assert.Equal(@"C:\tools", options.DevToolsPath);
        Assert.Equal([@"C:\a", @"C:\b"], options.Projects ?? []);
        Assert.Equal(["--nolog"], options.PassthroughArgs);
    }

    [Fact]
    public void Parse_UnknownArgs_AreIgnored()
    {
        Assert.Empty(ProxyOptions.Parse(["--totally-unknown", "value", "stray"]).PassthroughArgs);
    }

    [Fact]
    public void Parse_NoArgs_LeavesProxyEnabledWithNoOverrides()
    {
        var options = ProxyOptions.Parse([]);

        Assert.False(options.ProxyDisabled);
        Assert.Null(options.DevToolsPath);
        Assert.Null(options.Projects);
        Assert.Empty(options.PassthroughArgs);
    }
}
