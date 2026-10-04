using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// Position matching behind get_fixes / apply_fix. An AL pragma covers whole lines, so a live and a
/// suppressed diagnostic of one rule on one line cannot be built from a fixture cheaply; the ordering
/// is tested on the helper directly.
/// </summary>
public sealed class MatchPositionTests
{
    private sealed record Hit(string Name, int Line, int Column, bool Suppressed);

    private static (Hit? Hit, bool Suppressed) Match(int line, int column, params Hit[] hits) =>
        CodeFixRunner.MatchPosition(hits, h => (h.Line, h.Column), h => h.Suppressed, line, column);

    [Fact]
    public void ExactSuppressed_WinsOverSameLineLive()
    {
        var (hit, suppressed) = Match(5, 20,
            new Hit("live", 5, 3, false),
            new Hit("pragma", 5, 20, true));

        Assert.Equal("pragma", hit?.Name);
        Assert.True(suppressed);
    }

    [Fact]
    public void ExactLive_WinsOverExactSuppressed()
    {
        var (hit, suppressed) = Match(5, 3,
            new Hit("pragma", 5, 3, true),
            new Hit("live", 5, 3, false));

        Assert.Equal("live", hit?.Name);
        Assert.False(suppressed);
    }

    [Fact]
    public void SameLineLive_WinsOverSameLineSuppressed_WhenNoExactMatch()
    {
        var (hit, suppressed) = Match(5, 99,
            new Hit("pragma", 5, 3, true),
            new Hit("live", 5, 20, false));

        Assert.Equal("live", hit?.Name);
        Assert.False(suppressed);
    }

    [Fact]
    public void SameLineSuppressed_WhenOnlySuppressedOnLine()
    {
        var (hit, suppressed) = Match(5, 99,
            new Hit("other line", 6, 99, false),
            new Hit("pragma", 5, 3, true));

        Assert.Equal("pragma", hit?.Name);
        Assert.True(suppressed);
    }

    [Fact]
    public void NoHit_WhenNothingOnLine()
    {
        var (hit, suppressed) = Match(5, 3, new Hit("other line", 6, 3, false));

        Assert.Null(hit);
        Assert.False(suppressed);
    }
}
