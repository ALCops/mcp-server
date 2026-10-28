using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ALCops.Mcp.Tools;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// Fixes that change more than the diagnostic's own document. No ALCops provider does, so these use
/// the test-only providers in <c>TestFixProviders.cs</c> through the tools' <c>RunAsync</c> seam.
/// FixAllProject has LC0020 in PageA.al and PageB.al at 11:17 and none in PageC.al.
/// </summary>
public sealed class MultiDocumentFixTests
{
    private const string Rule = "LC0020";

    private sealed class Context : IDisposable
    {
        public string ProjectPath { get; } = TestAnalyzers.CopyFixtureWithAnalyzers("FixAllProject", "alcops-multidoc-test");
        public ProjectSessionManager SessionManager { get; } = new(new ProjectLoader());
        public CodeFixRunner Runner { get; } = new();
        public ProjectAnalyzerResolver Resolver { get; } = TestAnalyzers.CreateAnalyzerResolver().Resolver;

        public string PathOf(string name) => Path.Combine(ProjectPath, name);
        public string Read(string name) => File.ReadAllText(PathOf(name));
        public byte[] Bytes(string name) => File.ReadAllBytes(PathOf(name));

        public string[] TempFiles() =>
            Directory.GetFiles(ProjectPath, "*" + GuardedFileWriter.TempSuffix, SearchOption.AllDirectories);

        public Task<ModelContextProtocol.Protocol.CallToolResult> ApplyFixAsync(CodeFixProvider provider, string? key = null) =>
            ApplyFixTool.RunAsync(
                SessionManager, Runner, DecoratedProvider.Resolving(Resolver, provider), new GuardedFileWriter(),
                ProjectPath, PathOf("PageA.al"), Rule, 11, 17, key, null, dryRun: false, CancellationToken.None);

        public void Dispose()
        {
            SessionManager.Dispose();
            TestAnalyzers.TryDeleteDirectory(ProjectPath);
        }
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
    public async Task ApplyFix_FixEditingTwoFiles_WritesBoth()
    {
        using var ctx = new Context();

        var result = await ctx.ApplyFixAsync(new TwoDocumentFixProvider("PageC.al"));

        var root = ToolResultAssert.Ok(result);
        Assert.True(root.GetProperty("applied").GetBoolean(), ToolResultAssert.Text(result));
        Assert.Equal(1, CountOccurrences(ctx.Read("PageA.al"), "ApplicationArea = All;"));
        Assert.Contains(TwoDocumentFixProvider.Comment, ctx.Read("PageC.al"));
        Assert.Equal(2, CountOccurrences(ctx.Read("PageB.al"), "ApplicationArea = All;"));
        Assert.Empty(ctx.TempFiles());
    }

    [Fact]
    public async Task ApplyFixAll_FixEditingTwoFiles_ListsAndWritesTheSideEditedFile()
    {
        using var ctx = new Context();
        var provider = new TwoDocumentFixProvider("PageC.al");

        // Runner level: the side-edited file is a change with no diagnostics of its own.
        var session = await ctx.SessionManager.GetOrLoadProjectAsync(ctx.ProjectPath);
        var analyzers = new DecoratedProvider(await ctx.Resolver.ResolveAsync(ctx.ProjectPath, null), fixProviders: [provider]);
        var computed = await ctx.Runner.ApplyFixAllAsync(session, Rule, FixAllScope.Project, null, null, analyzers);

        Assert.Equal(FixAllStatus.Completed, computed.Status);
        Assert.Equal(["PageA.al", "PageB.al", "PageC.al"], computed.Changes.Select(c => Path.GetFileName(c.FilePath)));
        Assert.Empty(computed.Changes.Single(c => Path.GetFileName(c.FilePath) == "PageC.al").Diagnostics);
        Assert.Single(computed.Changes.Single(c => Path.GetFileName(c.FilePath) == "PageA.al").Diagnostics);

        // Tool level: every changed file is written, the side edits of both fixes included.
        var result = await ApplyFixAllTool.RunAsync(
            ctx.SessionManager, ctx.Runner, DecoratedProvider.Resolving(ctx.Resolver, provider), new GuardedFileWriter(),
            ctx.ProjectPath, Rule, "project", null, null, null, dryRun: false, CancellationToken.None);

        var root = ToolResultAssert.Ok(result);
        Assert.True(root.GetProperty("applied").GetBoolean(), ToolResultAssert.Text(result));
        var filesChanged = root.GetProperty("filesChanged").EnumerateArray()
            .Select(e => Path.GetFileName(e.GetString())).ToArray();
        Assert.Equal(["PageA.al", "PageB.al", "PageC.al"], filesChanged);
        Assert.Equal(1, CountOccurrences(ctx.Read("PageA.al"), "ApplicationArea = All;"));
        Assert.Equal(1, CountOccurrences(ctx.Read("PageB.al"), "ApplicationArea = All;"));
        Assert.Equal(2, CountOccurrences(ctx.Read("PageC.al"), TwoDocumentFixProvider.Comment));
        Assert.Empty(ctx.TempFiles());
    }

    [Fact]
    public async Task FixAddingAFile_IsNotFoundUnsupportedFix_AndWritesNothing()
    {
        using var ctx = new Context();
        string[] names = ["PageA.al", "PageB.al", "PageC.al", "Codeunit.al"];
        var before = names.ToDictionary(n => n, ctx.Bytes);

        var result = await ctx.ApplyFixAsync(new AddDocumentFixProvider());

        var root = ToolResultAssert.Error(result, "NotFound", "UnsupportedFix");
        Assert.Contains("adds or removes files", root.GetProperty("message").GetString());

        var fixAll = await ApplyFixAllTool.RunAsync(
            ctx.SessionManager, ctx.Runner, DecoratedProvider.Resolving(ctx.Resolver, new AddDocumentFixProvider()), new GuardedFileWriter(),
            ctx.ProjectPath, Rule, "project", null, null, null, dryRun: false, CancellationToken.None);
        ToolResultAssert.Error(fixAll, "NotFound", "UnsupportedFix");

        foreach (var name in names)
            Assert.Equal(before[name], ctx.Bytes(name));
        Assert.False(File.Exists(ctx.PathOf(AddDocumentFixProvider.NewFileName)));
        Assert.Empty(ctx.TempFiles());
    }

    [Fact]
    public async Task ApplyFix_SecondFileChangedButRefreshSkipsIt_ReturnsStaleAndWritesNeither()
    {
        using var ctx = new Context();

        // Prime the session so the cached texts are the originals.
        await ctx.SessionManager.GetOrLoadProjectAsync(ctx.ProjectPath);
        var pageABefore = ctx.Bytes("PageA.al");

        // A same-length edit with the old timestamp: the refresh's length+mtime gate skips PageC, so the
        // fix is computed from the cached text and the all-or-nothing write must refuse both files.
        var pageC = ctx.PathOf("PageC.al");
        var stamp = File.GetLastWriteTimeUtc(pageC);
        var content = await File.ReadAllTextAsync(pageC);
        Assert.Contains("50102", content);
        await File.WriteAllTextAsync(pageC, content.Replace("50102", "50109"));
        File.SetLastWriteTimeUtc(pageC, stamp);
        var pageCEdited = ctx.Bytes("PageC.al");

        var result = await ctx.ApplyFixAsync(new TwoDocumentFixProvider("PageC.al"));

        var root = ToolResultAssert.Error(result, "Stale");
        Assert.Equal(pageC, root.GetProperty("filePath").GetString());
        Assert.Contains(pageC, root.GetProperty("message").GetString());
        Assert.Equal(pageABefore, ctx.Bytes("PageA.al"));
        Assert.Equal(pageCEdited, ctx.Bytes("PageC.al"));
        Assert.Empty(ctx.TempFiles());
    }
}
