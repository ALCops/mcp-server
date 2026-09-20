using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.3.0-preview.9", "1.3.0-preview.10", -1)]
    [InlineData("1.3.0-preview.2", "1.3.0-preview.10", -1)]
    [InlineData("1.3.0-preview.1", "1.3.0", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1", -1)]
    [InlineData("1.0.0-beta", "1.0.0-BETA", 0)]
    [InlineData("1.0.0+sha.abc", "1.0.0", 0)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("0.4.1--no-branch-.1", "0.4.1", -1)]
    [InlineData("0.4.0", "0.4.1--no-branch-.1", -1)]
    [InlineData("1.0.0-preview.010", "1.0.0-preview.10", 0)]
    public void Compare_Theory(string a, string b, int expectedSign)
    {
        Assert.True(SemanticVersion.TryParse(a, out var va));
        Assert.True(SemanticVersion.TryParse(b, out var vb));

        var result = va.CompareTo(vb);
        Assert.Equal(expectedSign, Math.Sign(result));

        var reverse = vb.CompareTo(va);
        Assert.Equal(-expectedSign, Math.Sign(reverse));
    }

    [Fact]
    public void TryParse_OddButAccepted()
    {
        Assert.True(SemanticVersion.TryParse("0.4.1--no-branch-.1", out var v));
        Assert.Equal(new Version(0, 4, 1, 0), v.Base);
        Assert.Equal(["-no-branch-", "1"], v.PrereleaseIdentifiers);
        Assert.False(v.IsStable);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("also broken")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    public void TryParse_Rejects(string? input)
    {
        Assert.False(SemanticVersion.TryParse(input, out _));
    }
}
