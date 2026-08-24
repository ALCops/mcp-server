using ALCops.Mcp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// The startup bridge is the only thing that makes the child almcp agree with our fix tools about
/// which analyzers run and which rules are suppressed: almcp in MCP mode never reads
/// .vscode/settings.json, and has no per-call analyzer or ruleset parameter.
/// </summary>
public class WorkspaceStartupResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"alcops-startup-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private WorkspaceStartupResolver CreateResolver(params string[] projects)
    {
        var loader = new ExternalAnalyzerLoader(TestAnalyzers.ToolsLocator);
        return new WorkspaceStartupResolver(
            new ProjectAnalyzerResolver(loader, new RulesetLoader()),
            loader,
            NullLogger<WorkspaceStartupResolver>.Instance,
            projects.Length > 0 ? projects : null);
    }

    private string CreateProject(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "app.json"), """{"id":"","name":"Test","publisher":"T","version":"1.0.0.0"}""");
        return path;
    }

    [Fact]
    public void Discover_RootThatIsItselfAProject_WinsOutright()
    {
        var root = CreateProject();
        CreateProject("Nested");

        Assert.Equal([root], WorkspaceStartupResolver.DiscoverProjects(root));
    }

    [Fact]
    public void Discover_ScansDownwardForAppJson()
    {
        var app = CreateProject("src", "App");

        Assert.Equal([app], WorkspaceStartupResolver.DiscoverProjects(_root));
    }

    [Fact]
    public void Discover_SkipsExcludedDirectories()
    {
        CreateProject(".alpackages", "Vendor");
        CreateProject("node_modules", "Junk");
        var real = CreateProject("App");

        Assert.Equal([real], WorkspaceStartupResolver.DiscoverProjects(_root));
    }

    [Fact]
    public void Discover_StopsAtDepthFour()
    {
        CreateProject("a", "b", "c", "d", "e");

        Assert.Empty(WorkspaceStartupResolver.DiscoverProjects(_root));
    }

    [Fact]
    public void BuildAlMcpArgs_PassesAnalyzersAndRulesetFromProjectConfig()
    {
        var project = TestAnalyzers.CopyFixtureWithAnalyzers("FixAllRulesetProject", "alcops-startup-fixture");
        try
        {
            var args = CreateResolver(project).BuildAlMcpArgs([]);

            Assert.Equal(project, ValueOf(args, "--projects"));
            Assert.Equal("true", ValueOf(args, "--enablecodeanalysis"));

            // Same DLLs our own fix tools load, ';'-joined the way almcp parses them.
            var analyzers = ValueOf(args, "--codeanalyzers")!.Split(';');
            Assert.All(analyzers, a => Assert.True(File.Exists(a), a));
            foreach (var cop in TestAnalyzers.CopDllPaths)
                Assert.Contains(cop, analyzers);

            // The cops' shared dependency must travel with them: almcp resolves analyzer
            // dependencies only among the paths it is given, and a missing one turns every rule in
            // that assembly into an AD0001 "analyzer threw FileNotFoundException".
            Assert.Contains(
                Path.Combine(AppContext.BaseDirectory, "ALCops.Common.dll"),
                analyzers);

            // The fixture's custom.ruleset.json sets LC0020 to None — al_compile must see it too,
            // otherwise al_compile would report a diagnostic get_fixes refuses to fix.
            Assert.Equal(Path.Combine(project, "custom.ruleset.json"), ValueOf(args, "--rulesetpath"));
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public void BuildAlMcpArgs_UserSuppliedFlagsWin()
    {
        var project = TestAnalyzers.CopyFixtureWithAnalyzers("FixAllRulesetProject", "alcops-startup-override");
        try
        {
            var args = CreateResolver(project).BuildAlMcpArgs(["--rulesetpath", "override.ruleset.json"]);

            Assert.Equal("override.ruleset.json", ValueOf(args, "--rulesetpath"));
            Assert.Single(args, a => a == "--rulesetpath");

            // Everything the user didn't override is still supplied.
            Assert.Equal(project, ValueOf(args, "--projects"));
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public void BuildAlMcpArgs_ProjectWithoutAnalyzerConfig_OmitsCodeAnalysisFlags()
    {
        // A project that configures nothing must not get a bogus empty --codeanalyzers: almcp would
        // then implicitly enable code analysis with no analyzers loaded.
        var bare = CreateProject("Bare");
        var args = CreateResolver(bare).BuildAlMcpArgs([]);

        Assert.Equal(bare, ValueOf(args, "--projects"));
        Assert.DoesNotContain("--codeanalyzers", args);
        Assert.DoesNotContain("--enablecodeanalysis", args);
        Assert.DoesNotContain("--rulesetpath", args);
    }

    private static string? ValueOf(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
