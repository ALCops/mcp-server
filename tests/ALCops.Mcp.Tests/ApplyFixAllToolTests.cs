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
        public GuardedFileWriter Writer { get; set; } = new();

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

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020");

        var root = ToolResultAssert.Ok(result);

        Assert.True(root.GetProperty("applied").GetBoolean(), ToolResultAssert.Text(result));
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

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020", scope: "document", filePath: pageAPath);

        var root = ToolResultAssert.Ok(result);

        Assert.True(root.GetProperty("applied").GetBoolean(), ToolResultAssert.Text(result));
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

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020", dryRun: true);

        var root = ToolResultAssert.Ok(result);

        Assert.False(root.GetProperty("applied").GetBoolean(), ToolResultAssert.Text(result));
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

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "AC0012");

        var root = ToolResultAssert.Error(result, "Ambiguous");
        Assert.False(root.TryGetProperty("reason", out _));
        Assert.Equal("AC0012", root.GetProperty("diagnosticId").GetString());

        var candidates = root.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.True(candidates.Length > 1, ToolResultAssert.Text(result));
        Assert.All(candidates, c =>
        {
            Assert.Equal(JsonValueKind.String, c.GetProperty("equivalenceKey").ValueKind);
            Assert.False(string.IsNullOrEmpty(c.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrEmpty(c.GetProperty("providerName").GetString()));
        });

        var keys = candidates.Select(c => c.GetProperty("equivalenceKey").GetString()).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());

        // The codeunit must be untouched since no fix was chosen or applied.
        Assert.Contains("Access = Internal;", ctx.ReadFile("Codeunit.al"));
    }

    [Fact]
    public async Task ApplyFixAll_UnknownEquivalenceKey_ReturnsNotFoundWithCandidates()
    {
        using var ctx = new TestContext();

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "AC0012", equivalenceKey: "no-such-key");

        var root = ToolResultAssert.Error(result, "NotFound", "NoFixForEquivalenceKey");
        Assert.True(root.GetProperty("candidates").GetArrayLength() > 1, ToolResultAssert.Text(result));
        Assert.False(root.TryGetProperty("diagnosticsFound", out _));
        Assert.Contains("Access = Internal;", ctx.ReadFile("Codeunit.al"));
    }

    [Fact]
    public async Task ApplyFixAll_RuleWithoutFixProvider_ReturnsNotFoundNoFixProvider()
    {
        using var ctx = new TestContext();

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "ZZ9999");

        ToolResultAssert.Error(result, "NotFound", "NoFixProvider");
    }

    [Fact]
    public async Task ApplyFixAll_UnknownScope_ReturnsInvalid()
    {
        using var ctx = new TestContext();

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020", scope: "workspace");

        var root = ToolResultAssert.Error(result, "Invalid");
        Assert.Contains("workspace", root.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ApplyFixAll_DocumentScopeWithoutFilePath_ReturnsInvalid()
    {
        using var ctx = new TestContext();

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020", scope: "document");

        var root = ToolResultAssert.Error(result, "Invalid");
        Assert.Contains("filePath", root.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("C:\\bad\0file.al")]
    [InlineData("")]
    public async Task ApplyFixAll_DocumentScopeMalformedFilePath_ReturnsInvalid(string filePath)
    {
        using var ctx = new TestContext();

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020", scope: "document", filePath: filePath);

        var root = ToolResultAssert.Error(result, "Invalid");
        Assert.Contains("filePath", root.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ApplyFixAll_DocumentScopeFileOutsideProject_ReturnsNotFoundFileNotInProject()
    {
        using var ctx = new TestContext();
        var nope = Path.Combine(ctx.ProjectPath, "Nope.al");

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020", scope: "document", filePath: nope);

        var root = ToolResultAssert.Error(result, "NotFound", "FileNotInProject");
        Assert.Equal(Path.GetFullPath(nope), root.GetProperty("filePath").GetString());
    }

    [Fact]
    public async Task ApplyFixAll_NoDiagnosticsFound_ReturnsNotAppliedWithZeroCount()
    {
        using var ctx = new TestContext();
        var pageCPath = Path.Combine(ctx.ProjectPath, "PageC.al");

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020", scope: "document", filePath: pageCPath);

        var root = ToolResultAssert.Ok(result);

        Assert.False(root.GetProperty("applied").GetBoolean(), ToolResultAssert.Text(result));
        Assert.Equal(0, root.GetProperty("diagnosticsFound").GetInt32());
    }

    [Fact]
    public async Task ApplyFixAll_ExternalEditBetweenCalls_PreservesExternalEdit()
    {
        using var ctx = new TestContext();

        // Prime the session (loads all files into the workspace cache).
        await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020", dryRun: true);

        // External edit: rename OtherField → RenamedField in PageB.al.
        // The page is still valid and LC0020 still fires on the field-level ApplicationArea.
        var pageBPath = Path.Combine(ctx.ProjectPath, "PageB.al");
        var pageBContent = ctx.ReadFile("PageB.al");
        await File.WriteAllTextAsync(pageBPath, pageBContent.Replace("OtherField", "RenamedField"));

        // Apply for real — the refresh must pick up the rename.
        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020");

        var root = ToolResultAssert.Ok(result);
        Assert.True(root.GetProperty("applied").GetBoolean(), ToolResultAssert.Text(result));

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
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020", dryRun: true);

        // Add a new file with LC0020-triggering content.
        var pageDContent = ctx.ReadFile("PageB.al")
            .Replace("50101", "50103")
            .Replace("PageB", "PageD");
        await File.WriteAllTextAsync(Path.Combine(ctx.ProjectPath, "PageD.al"), pageDContent);

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020");

        var root = ToolResultAssert.Ok(result);
        Assert.True(root.GetProperty("applied").GetBoolean(), ToolResultAssert.Text(result));
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
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020", dryRun: true);

        // Delete PageB.al — only PageA still has LC0020.
        File.Delete(Path.Combine(ctx.ProjectPath, "PageB.al"));

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020");

        var root = ToolResultAssert.Ok(result);
        Assert.True(root.GetProperty("applied").GetBoolean(), ToolResultAssert.Text(result));
        Assert.Equal(1, root.GetProperty("diagnosticsFound").GetInt32());

        var filesChanged = root.GetProperty("filesChanged").EnumerateArray()
            .Select(e => Path.GetFileName(e.GetString())).ToArray();
        Assert.Single(filesChanged);
        Assert.Contains("PageA.al", filesChanged);
    }

    [Fact]
    public async Task ApplyFixAll_RulesetSuppressesRule_ReturnsNotFoundSuppressedByRuleset()
    {
        // FixAllRulesetProject ships a custom.ruleset.json setting LC0020 to "None",
        // even though PageA.al contains a redundant ApplicationArea occurrence.
        using var ctx = new TestContext("FixAllRulesetProject");
        var pageAPath = Path.Combine(ctx.ProjectPath, "PageA.al");
        var originalPageA = ctx.ReadFile("PageA.al");

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020", scope: "document", filePath: pageAPath);

        var root = ToolResultAssert.Error(result, "NotFound", "SuppressedByRuleset");
        Assert.Equal("LC0020", root.GetProperty("diagnosticId").GetString());
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
    public async Task ApplyFixAll_MixedEncodings_EachFileKeepsItsOwn()
    {
        using var ctx = new TestContext();

        // The BOM is added at test time; a committed fixture with a BOM could be mangled by git.
        var pageBPath = Path.Combine(ctx.ProjectPath, "PageB.al");
        await File.WriteAllBytesAsync(pageBPath, [0xEF, 0xBB, 0xBF, .. await File.ReadAllBytesAsync(pageBPath)]);

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020");

        var root = ToolResultAssert.Ok(result);
        Assert.True(root.GetProperty("applied").GetBoolean(), ToolResultAssert.Text(result));
        Assert.Equal(2, root.GetProperty("filesChanged").GetArrayLength());

        var pageB = await File.ReadAllBytesAsync(pageBPath);
        Assert.Equal([0xEF, 0xBB, 0xBF], pageB.Take(3));
        Assert.NotEqual(0xEF, pageB[3]); // exactly one BOM

        var pageA = await File.ReadAllBytesAsync(Path.Combine(ctx.ProjectPath, "PageA.al"));
        Assert.NotEqual(0xEF, pageA[0]); // a bare file gains no BOM

        Assert.Equal(1, CountOccurrences(ctx.ReadFile("PageA.al"), "ApplicationArea = All;"));
        Assert.Equal(1, CountOccurrences(ctx.ReadFile("PageB.al"), "ApplicationArea = All;"));
    }

    [Fact]
    public async Task ApplyFixAll_SecondCommitFails_RollsBackAndReportsAllThree()
    {
        using var ctx = new TestContext();

        // A third file with LC0020, so the failing commit sits between a committed and a pending file.
        var pageDContent = ctx.ReadFile("PageB.al").Replace("50101", "50103").Replace("PageB", "PageD");
        await File.WriteAllTextAsync(Path.Combine(ctx.ProjectPath, "PageD.al"), pageDContent);

        string[] names = ["PageA.al", "PageB.al", "PageD.al"];
        var before = names.ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(ctx.ProjectPath, n)));

        ctx.Writer = GuardedFileWriterTests.FailingOnCalls(2);

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020");

        var root = ToolResultAssert.Ok(result);

        Assert.False(root.GetProperty("applied").GetBoolean(), ToolResultAssert.Text(result));
        Assert.Equal(3, root.GetProperty("diagnosticsFound").GetInt32());
        Assert.Empty(root.GetProperty("filesChanged").EnumerateArray());

        var conflicts = root.GetProperty("conflicts").EnumerateArray()
            .Select(e => Path.GetFileName(e.GetProperty("filePath").GetString())).ToArray();
        Assert.Equal(3, conflicts.Length);
        Assert.Equal(names, conflicts.Order());

        // The JSON contract carries the conflict kind: one WriteFailed, one RolledBack, one NotWritten.
        var kinds = root.GetProperty("conflicts").EnumerateArray()
            .Select(e => e.GetProperty("kind").GetString()!).Order().ToArray();
        Assert.Equal(["NotWritten", "RolledBack", "WriteFailed"], kinds);

        var message = root.GetProperty("message").GetString();
        Assert.Contains("could not be written", message);
        Assert.Contains("restored", message);

        var unfixedFiles = root.GetProperty("unfixedDiagnostics").EnumerateArray()
            .Select(e => Path.GetFileName(e.GetProperty("filePath").GetString()!))
            .ToHashSet();
        Assert.Superset(names.ToHashSet(), unfixedFiles);

        foreach (var name in names)
            Assert.Equal(before[name], File.ReadAllBytes(Path.Combine(ctx.ProjectPath, name)));

        Assert.Empty(TempFiles(ctx.ProjectPath));
    }

    [Fact]
    public async Task ApplyFixAll_NoTempFilesLeftBehind()
    {
        using var ctx = new TestContext();

        var result = await ApplyFixAllTool.ApplyFixAll(
            ctx.SessionManager, ctx.CodeFixRunner, ctx.AnalyzerResolver, ctx.Writer,
            ctx.ProjectPath, "LC0020");

        var root = ToolResultAssert.Ok(result);
        Assert.True(root.GetProperty("applied").GetBoolean(), ToolResultAssert.Text(result));
        Assert.Empty(TempFiles(ctx.ProjectPath));
    }

    private static string[] TempFiles(string projectPath) =>
        Directory.GetFiles(projectPath, "*" + GuardedFileWriter.TempSuffix, SearchOption.AllDirectories);

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
    public void MergeUnfixed_ConflictFileDiagnosticAlreadyUnfixed_IsListedOnce()
    {
        // A diagnostic the fix-all pass could not fix is already in Unfixed; when its file is also
        // skipped as a conflict, the merge must not list it twice.
        var unfixable = new FixAllUnfixedDiagnostic("FileB.al", 10, 1);
        var fixable = new FixAllUnfixedDiagnostic("FileB.al", 20, 1);
        var result = new FixAllResult(
            FixAllStatus.Completed, "LC0001", 2, "Fix", "key",
            [new FixAllFileChange("FileB.al", "old", "new", [unfixable, fixable])],
            [], [unfixable]);

        var conflicts = new List<FileWriteConflict> { new("FileB.al", "conflict") };
        var merged = ApplyFixAllTool.MergeUnfixed(result, conflicts);

        Assert.Equal(2, merged.Count);
        Assert.Contains(unfixable, merged);
        Assert.Contains(fixable, merged);
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
