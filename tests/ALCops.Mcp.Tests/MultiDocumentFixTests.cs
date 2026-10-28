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

        public Task<ModelContextProtocol.Protocol.CallToolResult> ApplyFixAsync(
            CodeFixProvider provider, string? key = null, GuardedFileWriter? writer = null, bool dryRun = false) =>
            ApplyFixTool.RunAsync(
                SessionManager, Runner, DecoratedProvider.Resolving(Resolver, provider), writer ?? new GuardedFileWriter(),
                ProjectPath, PathOf("PageA.al"), Rule, 11, 17, key, null, dryRun, CancellationToken.None);

        public Task<ModelContextProtocol.Protocol.CallToolResult> ApplyFixAllAsync(CodeFixProvider provider, bool dryRun = false) =>
            ApplyFixAllTool.RunAsync(
                SessionManager, Runner, DecoratedProvider.Resolving(Resolver, provider), new GuardedFileWriter(),
                ProjectPath, Rule, "project", null, null, null, dryRun, CancellationToken.None);

        public static readonly string[] AllFiles = ["PageA.al", "PageB.al", "PageC.al", "Codeunit.al"];

        public Dictionary<string, byte[]> Snapshot() => AllFiles.ToDictionary(n => n, Bytes);

        /// <summary>
        /// A same-length edit of PageC.al with its old timestamp: the refresh's length+mtime gate skips
        /// it, so a fix is computed from the cached text and the write's stale check must catch it.
        /// Returns the edited bytes.
        /// </summary>
        public async Task<byte[]> MakePageCStaleAsync()
        {
            var pageC = PathOf("PageC.al");
            var stamp = File.GetLastWriteTimeUtc(pageC);
            var content = await File.ReadAllTextAsync(pageC);
            Assert.Contains("50102", content);
            await File.WriteAllTextAsync(pageC, content.Replace("50102", "50109"));
            File.SetLastWriteTimeUtc(pageC, stamp);
            return Bytes("PageC.al");
        }

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
            .Select(e => Path.GetFileName(e.GetString()!)).ToArray();
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
        Assert.Contains("adds, removes or renames files", root.GetProperty("message").GetString());

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

    [Fact]
    public async Task ApplyFix_FixEditingTwoFiles_DryRun_ReportsBothAndWritesNothing()
    {
        using var ctx = new Context();
        var before = ctx.Snapshot();

        var result = await ctx.ApplyFixAsync(new TwoDocumentFixProvider("PageC.al"), dryRun: true);

        var root = ToolResultAssert.Ok(result);
        Assert.False(root.GetProperty("applied").GetBoolean());
        Assert.True(root.GetProperty("dryRun").GetBoolean());
        var changes = root.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(["PageA.al", "PageC.al"], changes.Select(c => Path.GetFileName(c.GetProperty("filePath").GetString()!)));
        Assert.All(changes, c => Assert.NotEqual("", c.GetProperty("diff").GetString()));
        Assert.Contains("+" + TwoDocumentFixProvider.Comment, changes[1].GetProperty("diff").GetString());

        foreach (var name in Context.AllFiles)
            Assert.Equal(before[name], ctx.Bytes(name));
        Assert.Empty(ctx.TempFiles());
    }

    [Fact]
    public async Task ApplyFix_SecondFileNotValidUtf8_ReturnsFaultedUnsupportedEncodingAndWritesNeither()
    {
        using var ctx = new Context();

        // Append a comment holding a Windows-1252 "é" (0xE9) to PageC.al; the loader decodes it with
        // replacement, so the fix is still computed, but the strict decode at write time refuses it.
        var pageC = ctx.PathOf("PageC.al");
        byte[] pageCBytes = [.. await File.ReadAllBytesAsync(pageC), .. "\n// caf"u8, 0xE9, (byte)'\n'];
        await File.WriteAllBytesAsync(pageC, pageCBytes);
        var before = ctx.Snapshot();

        var result = await ctx.ApplyFixAsync(new TwoDocumentFixProvider("PageC.al"));

        var root = ToolResultAssert.Error(result, "Faulted", "UnsupportedEncoding");
        Assert.Equal(pageC, root.GetProperty("filePath").GetString());
        foreach (var name in Context.AllFiles)
            Assert.Equal(before[name], ctx.Bytes(name));
        Assert.Empty(ctx.TempFiles());
    }

    [Fact]
    public async Task ApplyFix_SecondCommitFails_ReturnsFaultedWriteFailedAndRestoresBoth()
    {
        using var ctx = new Context();
        var before = ctx.Snapshot();

        // Changes are committed in path order: PageA.al, then PageC.al, whose move fails.
        var result = await ctx.ApplyFixAsync(
            new TwoDocumentFixProvider("PageC.al"), writer: GuardedFileWriterTests.FailingOnCalls(2));

        var root = ToolResultAssert.Error(result, "Faulted", "WriteFailed");
        Assert.Contains("injected move failure", root.GetProperty("message").GetString());
        foreach (var name in Context.AllFiles)
            Assert.Equal(before[name], ctx.Bytes(name));
        Assert.Empty(ctx.TempFiles());
    }

    [Fact]
    public async Task ApplyFixAll_SideEditFileStale_WritesNothing()
    {
        using var ctx = new Context();
        var provider = new TwoDocumentFixProvider("PageC.al");

        // Prime the session (dry run) so the cached texts are the originals.
        ToolResultAssert.Ok(await ctx.ApplyFixAllAsync(provider, dryRun: true));
        var pageABefore = ctx.Bytes("PageA.al");
        var pageBBefore = ctx.Bytes("PageB.al");
        var pageCEdited = await ctx.MakePageCStaleAsync();

        var result = await ctx.ApplyFixAllAsync(provider);

        var root = ToolResultAssert.Ok(result);
        var text = ToolResultAssert.Text(result);
        Assert.False(root.GetProperty("applied").GetBoolean(), text);
        Assert.Empty(root.GetProperty("filesChanged").EnumerateArray());
        Assert.Empty(root.GetProperty("changes").EnumerateArray());

        var conflict = Assert.Single(root.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(ctx.PathOf("PageC.al"), conflict.GetProperty("filePath").GetString());
        Assert.Equal(FileWriteConflictKind.StaleFile, conflict.GetProperty("kind").GetString());

        var message = root.GetProperty("message").GetString();
        Assert.Contains("other files than the ones with occurrences", message);
        Assert.Contains("nothing was written", message);

        var unfixedFiles = root.GetProperty("unfixedDiagnostics").EnumerateArray()
            .Select(d => Path.GetFileName(d.GetProperty("filePath").GetString()!))
            .Distinct().Order().ToArray();
        Assert.Equal(["PageA.al", "PageB.al"], unfixedFiles);

        Assert.Equal(pageABefore, ctx.Bytes("PageA.al"));
        Assert.Equal(pageBBefore, ctx.Bytes("PageB.al"));
        Assert.Equal(pageCEdited, ctx.Bytes("PageC.al"));
        Assert.Empty(ctx.TempFiles());
    }

    // No rename test: the BC Workspaces API (18.0) has neither Solution.WithDocumentFilePath nor
    // Solution.WithDocumentName, so no provider can produce a changed document with another file
    // path. CollectChangesAsync still refuses one as UnsupportedFix.
}
