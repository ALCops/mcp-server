using System.Text.Json;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ALCops.Mcp.Tools;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// Tests for apply_fix_all (issue ALCops/mcp-server#15): fixing every occurrence of a
/// diagnostic rule across a project (or a single document) in one pass.
/// </summary>
public class ApplyFixAllToolTests
{
    private sealed class TestContext : IDisposable
    {
        public string ProjectPath { get; }
        public ProjectSessionManager SessionManager { get; }
        public CodeFixRunner CodeFixRunner { get; }
        public ProjectAnalyzerResolver AnalyzerResolver { get; }

        public TestContext(string fixtureName = "FixAllProject")
        {
            // The copy gets an al.codeAnalyzers setting pointing at the cop DLLs beside the test
            // binary — nothing is bundled any more, so a fixture without it finds zero diagnostics.
            ProjectPath = TestAnalyzers.CopyFixtureWithAnalyzers(fixtureName, "alcops-fixall-test");

            SessionManager = new ProjectSessionManager(new ProjectLoader());
            CodeFixRunner = new CodeFixRunner();
            AnalyzerResolver = TestAnalyzers.CreateAnalyzerResolver().Resolver;
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
        Assert.Empty(root.GetProperty("conflicts").EnumerateArray());

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

    [Fact]
    public async Task ApplyFixAll_ExternalEditBetweenCalls_PreservesExternalEdit()
    {
        using var ctx = new TestContext();

        // Prime the session (loads all files into the workspace cache).
        await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver,
            ctx.ProjectPath, "LC0020", dryRun: true);

        // External edit: rename OtherField → RenamedField in PageB.al.
        // The page is still valid and LC0020 still fires on the field-level ApplicationArea.
        var pageBPath = Path.Combine(ctx.ProjectPath, "PageB.al");
        var pageBContent = ctx.ReadFile("PageB.al");
        await File.WriteAllTextAsync(pageBPath, pageBContent.Replace("OtherField", "RenamedField"));

        // Apply for real — the refresh must pick up the rename.
        var resultJson = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver,
            ctx.ProjectPath, "LC0020");

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("applied").GetBoolean(), resultJson);

        // PageB must still contain the externally-renamed field AND have exactly one ApplicationArea.
        var pageBFinal = ctx.ReadFile("PageB.al");
        Assert.Contains("RenamedField", pageBFinal);
        Assert.Equal(1, CountOccurrences(pageBFinal, "ApplicationArea = All;"));
    }

    [Fact]
    public async Task ApplyFixAll_FileAddedAfterLoad_FixesNewFile()
    {
        using var ctx = new TestContext();

        // Prime to load the initial files.
        await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver,
            ctx.ProjectPath, "LC0020", dryRun: true);

        // Add a new file with LC0020-triggering content.
        var pageDContent = ctx.ReadFile("PageB.al")
            .Replace("50101", "50103")
            .Replace("PageB", "PageD");
        await File.WriteAllTextAsync(Path.Combine(ctx.ProjectPath, "PageD.al"), pageDContent);

        var resultJson = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver,
            ctx.ProjectPath, "LC0020");

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("applied").GetBoolean(), resultJson);
        Assert.Equal(3, root.GetProperty("diagnosticsFound").GetInt32());

        var filesChanged = root.GetProperty("filesChanged").EnumerateArray()
            .Select(e => Path.GetFileName(e.GetString())).ToArray();
        Assert.Contains("PageD.al", filesChanged);
    }

    [Fact]
    public async Task ApplyFixAll_FileDeletedAfterLoad_FixesRemainingFiles()
    {
        using var ctx = new TestContext();

        // Prime to load the initial files.
        await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver,
            ctx.ProjectPath, "LC0020", dryRun: true);

        // Delete PageB.al — only PageA still has LC0020.
        File.Delete(Path.Combine(ctx.ProjectPath, "PageB.al"));

        var resultJson = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver,
            ctx.ProjectPath, "LC0020");

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("applied").GetBoolean(), resultJson);
        Assert.Equal(1, root.GetProperty("diagnosticsFound").GetInt32());

        var filesChanged = root.GetProperty("filesChanged").EnumerateArray()
            .Select(e => Path.GetFileName(e.GetString())).ToArray();
        Assert.Single(filesChanged);
        Assert.Contains("PageA.al", filesChanged);
    }

    [Fact]
    public async Task ApplyFixAll_RulesetSuppressesRule_TreatsItAsZeroDiagnostics()
    {
        // FixAllRulesetProject ships a custom.ruleset.json setting LC0020 to "None",
        // even though PageA.al contains a redundant ApplicationArea occurrence.
        using var ctx = new TestContext("FixAllRulesetProject");
        var pageAPath = Path.Combine(ctx.ProjectPath, "PageA.al");
        var originalPageA = ctx.ReadFile("PageA.al");

        var resultJson = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver,
            ctx.ProjectPath, "LC0020", scope: "document", filePath: pageAPath);

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("applied").GetBoolean(), resultJson);
        Assert.Equal(0, root.GetProperty("diagnosticsFound").GetInt32());
        Assert.Equal(originalPageA, ctx.ReadFile("PageA.al"));
    }

    [Fact]
    public async Task ApplyFixAll_Changes_CarryDiagnosticLocations()
    {
        using var ctx = new TestContext();
        var session = await ctx.SessionManager.GetOrLoadProjectAsync(ctx.ProjectPath);
        var analyzerSet = await ctx.AnalyzerResolver.ResolveAsync(ctx.ProjectPath, null);

        var result = await ctx.CodeFixRunner.ApplyFixAllAsync(
            session, "LC0020", FixAllScope.Project, null, null, analyzerSet);

        Assert.Equal(FixAllStatus.Completed, result.Status);
        Assert.Equal(2, result.Changes.Count);

        var pageAChange = result.Changes.Single(c => Path.GetFileName(c.FilePath) == "PageA.al");
        Assert.Single(pageAChange.Diagnostics);
        Assert.Equal(11, pageAChange.Diagnostics[0].Line);

        var pageBChange = result.Changes.Single(c => Path.GetFileName(c.FilePath) == "PageB.al");
        Assert.Single(pageBChange.Diagnostics);
        Assert.Equal(11, pageBChange.Diagnostics[0].Line);
    }

    [Fact]
    public void MergeUnfixed_NoConflicts_ReturnsIdenticalList()
    {
        var unfixed = new List<FixAllUnfixedDiagnostic>
        {
            new("FileA.al", 5, 1)
        };
        var result = new FixAllResult(
            FixAllStatus.Completed, "LC0001", 2, "Fix", "key",
            [new FixAllFileChange("FileB.al", "old", "new", [new("FileB.al", 10, 1)])],
            [], unfixed);

        var merged = ApplyFixAllTool.MergeUnfixed(result, []);

        Assert.Same(result.Unfixed, merged);
    }

    [Fact]
    public void MergeUnfixed_WithConflict_AppendsDiagnosticsFromConflictedFile()
    {
        var unfixed = new List<FixAllUnfixedDiagnostic> { new("FileA.al", 5, 1) };
        var fileBDiag = new FixAllUnfixedDiagnostic("FileB.al", 10, 1);
        var result = new FixAllResult(
            FixAllStatus.Completed, "LC0001", 3, "Fix", "key",
            [
                new FixAllFileChange("FileA.al", "old", "new", [new("FileA.al", 3, 1)]),
                new FixAllFileChange("FileB.al", "old", "new", [fileBDiag]),
            ],
            [], unfixed);

        var conflicts = new List<FileWriteConflict> { new("FileB.al", "conflict") };
        var merged = ApplyFixAllTool.MergeUnfixed(result, conflicts);

        Assert.Equal(2, merged.Count);
        Assert.Equal(unfixed[0], merged[0]);
        Assert.Equal(fileBDiag, merged[1]);
    }

    [Fact]
    public void MergeUnfixed_CaseInsensitivePathMatch()
    {
        var fileBDiag = new FixAllUnfixedDiagnostic("C:\\Src\\FileB.al", 10, 1);
        var result = new FixAllResult(
            FixAllStatus.Completed, "LC0001", 1, "Fix", "key",
            [new FixAllFileChange("C:\\Src\\FileB.al", "old", "new", [fileBDiag])],
            [], []);

        var conflicts = new List<FileWriteConflict> { new("c:\\src\\fileb.al", "conflict") };
        var merged = ApplyFixAllTool.MergeUnfixed(result, conflicts);

        Assert.Single(merged);
        Assert.Equal(fileBDiag, merged[0]);
    }
}
