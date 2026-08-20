using System.Text.Json;
using ALCops.Mcp.Services;
using ALCops.Mcp.Tools;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// Tests for apply_fix_all (issue ALCops/mcp-server#15): fixing every occurrence of a
/// diagnostic rule across a project (or a single document) in one pass.
/// </summary>
public class ApplyFixAllToolTests
{
    private static string GetFixturePath(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(
                $"Test fixture '{name}' not found at {path}. Ensure fixtures are copied to output.");
        return path;
    }

    private static ProjectAnalyzerResolver CreateAnalyzerResolver(AnalyzerRegistry registry)
    {
        var devToolsLocator = new DevToolsLocator();
        var alExtensionLocator = new AlExtensionLocator();
        var nugetDownloader = new NuGetDevToolsDownloader();
        var externalLoader = new ExternalAnalyzerLoader(alExtensionLocator, nugetDownloader, devToolsLocator);
        var rulesetLoader = new RulesetLoader();
        return new ProjectAnalyzerResolver(registry, externalLoader, rulesetLoader);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
    }

    private sealed class TestContext : IDisposable
    {
        public string ProjectPath { get; }
        public ProjectSessionManager SessionManager { get; }
        public CodeFixRunner CodeFixRunner { get; }
        public ProjectAnalyzerResolver AnalyzerResolver { get; }

        public TestContext()
        {
            var fixtureSource = GetFixturePath("FixAllProject");
            ProjectPath = Path.Combine(Path.GetTempPath(), $"alcops-fixall-test-{Guid.NewGuid():N}");
            CopyDirectory(fixtureSource, ProjectPath);

            var registry = new AnalyzerRegistry();
            SessionManager = new ProjectSessionManager(new ProjectLoader(new DevToolsLocator()));
            CodeFixRunner = new CodeFixRunner(registry);
            AnalyzerResolver = CreateAnalyzerResolver(registry);
        }

        public string ReadFile(string name) => File.ReadAllText(Path.Combine(ProjectPath, name));

        public void Dispose()
        {
            SessionManager.Dispose();
            if (Directory.Exists(ProjectPath))
                Directory.Delete(ProjectPath, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyFixAll_ProjectScope_FixesAllOccurrencesAcrossFiles()
    {
        using var ctx = new TestContext();

        var resultJson = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver,
            ctx.ProjectPath, "LC0020");

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("applied").GetBoolean(), resultJson);
        Assert.Equal(2, root.GetProperty("diagnosticsFound").GetInt32());

        var filesChanged = root.GetProperty("filesChanged").EnumerateArray()
            .Select(e => Path.GetFileName(e.GetString())).ToArray();
        Assert.Contains("PageA.al", filesChanged);
        Assert.Contains("PageB.al", filesChanged);

        // PageA had two "ApplicationArea = All;" occurrences (page-level + field-level);
        // only the redundant field-level one should be removed by the fix.
        var pageAOccurrences = CountOccurrences(ctx.ReadFile("PageA.al"), "ApplicationArea = All;");
        Assert.Equal(1, pageAOccurrences);

        var pageBOccurrences = CountOccurrences(ctx.ReadFile("PageB.al"), "ApplicationArea = All;");
        Assert.Equal(1, pageBOccurrences);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    [Fact]
    public async Task ApplyFixAll_DocumentScope_OnlyFixesTargetFile()
    {
        using var ctx = new TestContext();
        var pageAPath = Path.Combine(ctx.ProjectPath, "PageA.al");

        var resultJson = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver,
            ctx.ProjectPath, "LC0020", scope: "document", filePath: pageAPath);

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("applied").GetBoolean(), resultJson);
        Assert.Equal(1, root.GetProperty("diagnosticsFound").GetInt32());

        var filesChanged = root.GetProperty("filesChanged").EnumerateArray()
            .Select(e => Path.GetFileName(e.GetString())).ToArray();
        Assert.Single(filesChanged);
        Assert.Contains("PageA.al", filesChanged);

        // PageB must remain untouched since scope was restricted to PageA.
        Assert.Contains("ApplicationArea = All;", ctx.ReadFile("PageB.al"));
    }

    [Fact]
    public async Task ApplyFixAll_DryRun_ReportsChangesWithoutWriting()
    {
        using var ctx = new TestContext();
        var originalPageA = ctx.ReadFile("PageA.al");
        var originalPageB = ctx.ReadFile("PageB.al");

        var resultJson = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver,
            ctx.ProjectPath, "LC0020", dryRun: true);

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("applied").GetBoolean(), resultJson);
        Assert.True(root.GetProperty("dryRun").GetBoolean());
        Assert.Equal(2, root.GetProperty("diagnosticsFound").GetInt32());
        Assert.Equal(2, root.GetProperty("filesChanged").GetArrayLength());

        // Files on disk must be untouched.
        Assert.Equal(originalPageA, ctx.ReadFile("PageA.al"));
        Assert.Equal(originalPageB, ctx.ReadFile("PageB.al"));
    }

    [Fact]
    public async Task ApplyFixAll_MultipleDistinctFixesWithoutEquivalenceKey_ReturnsAmbiguousError()
    {
        using var ctx = new TestContext();

        var resultJson = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver,
            ctx.ProjectPath, "AC0012");

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        Assert.Equal("AmbiguousFix", root.GetProperty("error").GetString());
        var keys = root.GetProperty("availableEquivalenceKeys").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.True(keys.Length > 1, resultJson);

        // The codeunit must be untouched since no fix was chosen or applied.
        Assert.Contains("Access = Internal;", ctx.ReadFile("Codeunit.al"));
    }

    [Fact]
    public async Task ApplyFixAll_NoDiagnosticsFound_ReturnsNotAppliedWithZeroCount()
    {
        using var ctx = new TestContext();
        var pageCPath = Path.Combine(ctx.ProjectPath, "PageC.al");

        var resultJson = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver,
            ctx.ProjectPath, "LC0020", scope: "document", filePath: pageCPath);

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("applied").GetBoolean(), resultJson);
        Assert.Equal(0, root.GetProperty("diagnosticsFound").GetInt32());
    }
}
