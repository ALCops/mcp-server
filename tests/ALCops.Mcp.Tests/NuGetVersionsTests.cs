using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

public class NuGetVersionsTests
{
    [Fact]
    public void Parse_FindsLatestStable_And_HighestOverall()
    {
        var json = """{"versions":["0.4.1--no-branch-.1","1.0.0","1.1.0","1.2.0","1.3.0-preview.1"]}""";

        var (latest, prerelease) = NuGetVersions.Parse(json);

        Assert.Equal("1.2.0", latest);
        Assert.Equal("1.3.0-preview.1", prerelease);
    }

    [Fact]
    public void Parse_StableIsHighestOverall_WhenNoPrerelease()
    {
        var json = """{"versions":["1.0.0","1.1.0","1.2.0"]}""";

        var (latest, prerelease) = NuGetVersions.Parse(json);

        Assert.Equal("1.2.0", latest);
        Assert.Equal("1.2.0", prerelease);
    }

    [Fact]
    public void Parse_MalformedEntries_AreSkipped()
    {
        var json = """{"versions":["not-a-version","also broken","1.0.0"]}""";

        var (latest, prerelease) = NuGetVersions.Parse(json);

        Assert.Equal("1.0.0", latest);
        Assert.Equal("1.0.0", prerelease);
    }

    [Fact]
    public void Parse_EmptyVersionsList_ReturnsNulls()
    {
        var json = """{"versions":[]}""";

        var (latest, prerelease) = NuGetVersions.Parse(json);

        Assert.Null(latest);
        Assert.Null(prerelease);
    }

    [Fact]
    public void Parse_OnlyPrereleases_LatestIsNull()
    {
        var json = """{"versions":["1.0.0-alpha","2.0.0-beta"]}""";

        var (latest, prerelease) = NuGetVersions.Parse(json);

        Assert.Null(latest);
        Assert.Equal("2.0.0-beta", prerelease);
    }

    [Fact]
    public void Parse_MissingVersionsProperty_ReturnsNulls()
    {
        var json = """{"other":"data"}""";

        var (latest, prerelease) = NuGetVersions.Parse(json);

        Assert.Null(latest);
        Assert.Null(prerelease);
    }
}
