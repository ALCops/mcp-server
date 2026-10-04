using ALCops.Mcp.Services;
using ALCops.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>list_rules: success shapes, project validation shared with analyze (issue #33), and the catch-all.</summary>
public sealed class ListRulesToolTests : IDisposable
{
    private readonly string _projectPath = TestAnalyzers.CopyFixtureWithAnalyzers("ApplyFixProject", "alcops-listrules-test");

    public void Dispose() => TestAnalyzers.TryDeleteDirectory(_projectPath);

    private static (ProjectAnalyzerResolver Analyzers, WorkspaceStartupResolver Workspace) Resolvers(params string[] projects)
    {
        var (analyzerResolver, loader) = TestAnalyzers.CreateAnalyzerResolver();
        var workspace = new WorkspaceStartupResolver(
            analyzerResolver, loader, NullLogger<WorkspaceStartupResolver>.Instance, projects);
        return (analyzerResolver, workspace);
    }

    /// <summary>A resolver whose only --projects entry does not exist, so it has no projects at all.</summary>
    private static (ProjectAnalyzerResolver Analyzers, WorkspaceStartupResolver Workspace) NoProjects() =>
        Resolvers(Path.Combine(Path.GetTempPath(), $"alcops-listrules-missing-{Guid.NewGuid():N}"));

    [Fact]
    public async Task Ok_Compact_ListsRulesOfTheStartupProject()
    {
        var (analyzers, workspace) = Resolvers(_projectPath);

        var result = await ListRulesTool.ListRules(analyzers, workspace);

        var root = ToolResultAssert.Ok(result);
        var rules = root.GetProperty("rules").EnumerateArray().ToList();
        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => r.GetProperty("id").GetString() == "LC0020");
        Assert.All(rules, r =>
        {
            Assert.True(r.TryGetProperty("title", out _));
            Assert.True(r.TryGetProperty("cop", out _));
            Assert.False(r.TryGetProperty("hasCodeFix", out _));
        });
    }

    [Fact]
    public async Task Ok_Verbose_IncludesFullMetadata()
    {
        var (analyzers, workspace) = Resolvers(_projectPath);

        var result = await ListRulesTool.ListRules(analyzers, workspace, projectPath: _projectPath, verbose: true);

        var root = ToolResultAssert.Ok(result);
        var lc0020 = root.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "LC0020");
        Assert.True(lc0020.GetProperty("hasCodeFix").GetBoolean());
        Assert.True(lc0020.TryGetProperty("severity", out _));
        Assert.True(lc0020.TryGetProperty("copName", out _));
    }

    [Fact]
    public async Task Invalid_WhenExplicitProjectIsNotAStartupProject()
    {
        var (analyzers, workspace) = Resolvers(_projectPath);
        var other = Path.Combine(Path.GetTempPath(), "SomeOtherProject");

        var result = await ListRulesTool.ListRules(analyzers, workspace, projectPath: other);

        var root = ToolResultAssert.Error(result, "Invalid");
        var message = root.GetProperty("message").GetString()!;
        Assert.Contains(_projectPath, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SomeOtherProject", message);
    }

    [Fact]
    public async Task Invalid_WithNoStartupProjects_AndExplicitPath_DoesNotRenderEmptyList()
    {
        var (analyzers, workspace) = NoProjects();
        Assert.Empty(workspace.Config.ProjectDirectories);

        var result = await ListRulesTool.ListRules(analyzers, workspace, projectPath: _projectPath);

        var root = ToolResultAssert.Error(result, "Invalid");
        var message = root.GetProperty("message").GetString()!;
        Assert.DoesNotContain("started with: .", message);
        Assert.Contains("No AL project available", message);
    }

    [Fact]
    public async Task Invalid_WithNoStartupProjects_AndNoPath()
    {
        var (analyzers, workspace) = NoProjects();

        var result = await ListRulesTool.ListRules(analyzers, workspace);

        var root = ToolResultAssert.Error(result, "Invalid");
        Assert.Contains("No AL project available", root.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Faulted_WhenProjectPathCannotBeNormalized()
    {
        var (analyzers, workspace) = Resolvers(_projectPath);

        // An embedded NUL makes Path.GetFullPath throw ArgumentException; list_rules must catch it.
        var result = await ListRulesTool.ListRules(analyzers, workspace, projectPath: "C:\\bad\0path");

        var root = ToolResultAssert.Error(result, "Faulted");
        Assert.Equal("System.ArgumentException", root.GetProperty("detail").GetString());
        Assert.False(root.TryGetProperty("reason", out _));
    }
}
