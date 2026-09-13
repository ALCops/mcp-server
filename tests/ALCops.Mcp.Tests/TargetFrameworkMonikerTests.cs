using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

public class TargetFrameworkMonikerTests
{
    [Theory]
    [InlineData(".NETCoreApp,Version=v10.0", "net10.0")]
    [InlineData(".NETCoreApp,Version=v8.0", "net8.0")]
    [InlineData(".NETCoreApp,Version=v6.0", "net6.0")]
    [InlineData(".NETStandard,Version=v2.1", "netstandard2.1")]
    [InlineData(".NETStandard,Version=v2.0", "netstandard2.0")]
    public void ShortName_ConvertsLongFrameworkName(string input, string expected)
    {
        Assert.Equal(expected, TargetFrameworkMoniker.ShortName(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UnknownFramework,Version=v1.0")]
    public void ShortName_ReturnsNull_ForUnrecognisedInput(string? input)
    {
        Assert.Null(TargetFrameworkMoniker.ShortName(input));
    }

    [Fact]
    public void FindBestLibFolder_ExactMatch()
    {
        Assert.Equal("net10.0", TargetFrameworkMoniker.FindBestLibFolder(["net10.0", "net8.0"], "net10.0"));
    }

    [Fact]
    public void FindBestLibFolder_Net10FallsBackToNet8()
    {
        Assert.Equal("net8.0", TargetFrameworkMoniker.FindBestLibFolder(["net8.0"], "net10.0"));
    }

    [Fact]
    public void FindBestLibFolder_Net10PicksHighestAvailableFallback()
    {
        Assert.Equal("net9.0", TargetFrameworkMoniker.FindBestLibFolder(["net8.0", "net9.0"], "net10.0"));
    }

    [Fact]
    public void FindBestLibFolder_FallsToNetstandardWhenNoNetMatch()
    {
        Assert.Equal("netstandard2.1", TargetFrameworkMoniker.FindBestLibFolder(["netstandard2.1"], "net10.0"));
    }

    [Fact]
    public void FindBestLibFolder_NetstandardTarget_LowestHigherOrEqualMinorWins()
    {
        Assert.Equal("netstandard2.1",
            TargetFrameworkMoniker.FindBestLibFolder(["netstandard2.0", "netstandard2.1"], "netstandard2.1"));
    }

    [Fact]
    public void FindBestLibFolder_NetstandardTarget_HigherMinorAccepted()
    {
        Assert.Equal("netstandard2.1",
            TargetFrameworkMoniker.FindBestLibFolder(["netstandard2.1"], "netstandard2.0"));
    }

    [Fact]
    public void FindBestLibFolder_ReturnsNull_WhenNothingCompatible()
    {
        Assert.Null(TargetFrameworkMoniker.FindBestLibFolder(["net5.0"], "net10.0"));
    }

    [Fact]
    public void FindBestLibFolder_ReturnsNull_WhenEmpty()
    {
        Assert.Null(TargetFrameworkMoniker.FindBestLibFolder([], "net10.0"));
    }

    [Fact]
    public void FindBestLibFolder_CaseInsensitive()
    {
        Assert.NotNull(TargetFrameworkMoniker.FindBestLibFolder(["Net10.0"], "net10.0"));
    }
}
