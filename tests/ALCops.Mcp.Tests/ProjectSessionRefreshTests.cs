using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

public class ProjectSessionRefreshTests : IDisposable
{
    private readonly string _projectPath;
    private readonly ProjectSessionManager _sessionManager;

    public ProjectSessionRefreshTests()
    {
        _projectPath = TestAnalyzers.CopyFixtureWithAnalyzers("FixAllProject", "alcops-refresh-test");
        _sessionManager = new ProjectSessionManager(new ProjectLoader());
    }

    public void Dispose()
    {
        _sessionManager.Dispose();
        TestAnalyzers.TryDeleteDirectory(_projectPath);
    }

    [Fact]
    public async Task NothingChanged_SummaryAllZero_SameSolution()
    {
        var session = await _sessionManager.GetOrLoadProjectAsync(_projectPath);
        var solutionBefore = session.GetProject().Solution;
        var docIdsBefore = session.FilePathToDocumentId.Values.ToHashSet();

        var summary = await session.RefreshFromDiskAsync();

        Assert.Equal(0, summary.Updated);
        Assert.Equal(0, summary.Added);
        Assert.Equal(0, summary.Removed);
        Assert.False(summary.Any);
        Assert.Same(solutionBefore, session.GetProject().Solution);
        Assert.True(docIdsBefore.SetEquals(session.FilePathToDocumentId.Values));
    }

    [Fact]
    public async Task EditFile_UpdatedCountIsOne_NewTextVisible()
    {
        var session = await _sessionManager.GetOrLoadProjectAsync(_projectPath);
        var pageBPath = Path.Combine(_projectPath, "PageB.al");
        var docIdBefore = session.FilePathToDocumentId[Path.GetFullPath(pageBPath)];
        var pageADocId = session.FilePathToDocumentId[Path.GetFullPath(Path.Combine(_projectPath, "PageA.al"))];

        var content = await File.ReadAllTextAsync(pageBPath);
        await File.WriteAllTextAsync(pageBPath, content + "\n// edited");

        var summary = await session.RefreshFromDiskAsync();

        Assert.Equal(1, summary.Updated);
        Assert.Equal(0, summary.Added);
        Assert.Equal(0, summary.Removed);

        var doc = session.GetDocument(pageBPath);
        Assert.NotNull(doc);
        var text = (await doc!.GetTextAsync()).ToString();
        Assert.EndsWith("// edited", text);

        // DocumentIds must be preserved for both edited and untouched files.
        Assert.Equal(docIdBefore, session.FilePathToDocumentId[Path.GetFullPath(pageBPath)]);
        Assert.Equal(pageADocId, session.FilePathToDocumentId[Path.GetFullPath(Path.Combine(_projectPath, "PageA.al"))]);
    }

    [Fact]
    public async Task AddFile_AddedCountIsOne_DocumentAccessible()
    {
        var session = await _sessionManager.GetOrLoadProjectAsync(_projectPath);
        var initialCount = session.GetProject().Documents.Count();

        var pageDPath = Path.Combine(_projectPath, "PageD.al");
        await File.WriteAllTextAsync(pageDPath, """
            page 50103 PageD
            {
                ApplicationArea = All;

                layout
                {
                    area(content)
                    {
                        field(NewField; NewField)
                        {
                        }
                    }
                }

                var
                    NewField: Text;
            }
            """);

        var summary = await session.RefreshFromDiskAsync();

        Assert.Equal(1, summary.Added);
        Assert.Equal(0, summary.Updated);
        Assert.Equal(0, summary.Removed);
        Assert.NotNull(session.GetDocument(pageDPath));
        Assert.Equal(initialCount + 1, session.GetProject().Documents.Count());
    }

    [Fact]
    public async Task DeleteFile_RemovedCountIsOne_CompilationSucceeds()
    {
        var session = await _sessionManager.GetOrLoadProjectAsync(_projectPath);
        var pageCPath = Path.Combine(_projectPath, "PageC.al");

        File.Delete(pageCPath);

        var summary = await session.RefreshFromDiskAsync();

        Assert.Equal(1, summary.Removed);
        Assert.Equal(0, summary.Updated);
        Assert.Equal(0, summary.Added);
        Assert.Null(session.GetDocument(pageCPath));

        var compilation = await session.GetCompilationAsync();
        Assert.NotNull(compilation);
    }

    [Fact]
    public async Task TouchWithoutContentChange_SummaryZero_SameSolution()
    {
        var session = await _sessionManager.GetOrLoadProjectAsync(_projectPath);
        var solutionBefore = session.GetProject().Solution;
        var pageBPath = Path.Combine(_projectPath, "PageB.al");

        File.SetLastWriteTimeUtc(pageBPath, DateTime.UtcNow + TimeSpan.FromMinutes(1));

        var summary = await session.RefreshFromDiskAsync();

        Assert.Equal(0, summary.Updated);
        Assert.Equal(0, summary.Added);
        Assert.Equal(0, summary.Removed);
        Assert.Same(solutionBefore, session.GetProject().Solution);
    }

    [Fact]
    public async Task TouchWithoutContentChange_StampConverges()
    {
        var session = await _sessionManager.GetOrLoadProjectAsync(_projectPath);
        var pageBPath = Path.GetFullPath(Path.Combine(_projectPath, "PageB.al"));

        var newTime = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        File.SetLastWriteTimeUtc(pageBPath, newTime);
        var expectedStamp = FileStamp.Of(new FileInfo(pageBPath));

        var summary1 = await session.RefreshFromDiskAsync();

        Assert.False(summary1.Any);
        Assert.Equal(expectedStamp, session.TrackedDocuments[pageBPath].Stamp);

        var summary2 = await session.RefreshFromDiskAsync();
        Assert.False(summary2.Any);
    }

    [Fact]
    public async Task FileInAlPackages_NotAdded()
    {
        var session = await _sessionManager.GetOrLoadProjectAsync(_projectPath);
        var countBefore = session.GetProject().Documents.Count();

        var alPackagesDir = Path.Combine(_projectPath, ".alpackages");
        Directory.CreateDirectory(alPackagesDir);
        await File.WriteAllTextAsync(Path.Combine(alPackagesDir, "X.al"), "// should be excluded");

        var summary = await session.RefreshFromDiskAsync();

        Assert.Equal(0, summary.Added);
        Assert.Equal(countBefore, session.GetProject().Documents.Count());
    }

    [Fact]
    public async Task ViaSessionManager_EditVisibleOnSecondCall_SameInstance()
    {
        var session1 = await _sessionManager.GetOrLoadProjectAsync(_projectPath);

        var pageBPath = Path.Combine(_projectPath, "PageB.al");
        var content = await File.ReadAllTextAsync(pageBPath);
        await File.WriteAllTextAsync(pageBPath, content + "\n// externally edited");

        var session2 = await _sessionManager.GetOrLoadProjectAsync(_projectPath);

        Assert.Same(session1, session2);
        var doc = session2.GetDocument(pageBPath);
        Assert.NotNull(doc);
        var text = (await doc!.GetTextAsync()).ToString();
        Assert.Contains("// externally edited", text);
    }

    [Fact]
    public async Task LockedFile_RefreshCompletes_FileSkipped()
    {
        // FileShare.None is advisory on Linux; this test is meaningful only on Windows.
        if (!OperatingSystem.IsWindows())
            return;

        var session = await _sessionManager.GetOrLoadProjectAsync(_projectPath);
        var pageBPath = Path.Combine(_projectPath, "PageB.al");

        // Change timestamp so the stamp differs and refresh will attempt to read the file.
        File.SetLastWriteTimeUtc(pageBPath, DateTime.UtcNow + TimeSpan.FromMinutes(10));

        // Hold the file open exclusively — File.ReadAllTextAsync will throw IOException.
        using var lockStream = new FileStream(pageBPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var summary = await session.RefreshFromDiskAsync();

        // The file was skipped (not updated), no exception escaped.
        Assert.Equal(0, summary.Updated);
        Assert.Equal(0, summary.Added);
        Assert.Equal(0, summary.Removed);
    }

    [Fact]
    public async Task CancelledToken_ThrowsAndLeavesStateConsistent()
    {
        var session = await _sessionManager.GetOrLoadProjectAsync(_projectPath);
        var initialCount = session.GetProject().Documents.Count();

        await File.WriteAllTextAsync(Path.Combine(_projectPath, "PageD.al"), """
            page 50103 PageD
            {
                ApplicationArea = All;
                layout { area(content) { } }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(_projectPath, "PageE.al"), """
            page 50104 PageE
            {
                ApplicationArea = All;
                layout { area(content) { } }
            }
            """);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.RefreshFromDiskAsync(cts.Token));

        var summary = await session.RefreshFromDiskAsync();

        Assert.Equal(2, summary.Added);
        Assert.Equal(initialCount + 2, session.GetProject().Documents.Count());
    }

    [Fact]
    public async Task Dispose_Idempotent()
    {
        var session = await _sessionManager.GetOrLoadProjectAsync(_projectPath);
        session.Dispose();
        session.Dispose();
    }

    [Fact]
    public void TryStat_NonexistentPath_ReturnsNull()
    {
        var result = ProjectSession.TryStat(Path.Combine(_projectPath, "Nonexistent.al"));
        Assert.Null(result);
    }

    [Fact]
    public async Task TryReadAsync_NonexistentPath_ReturnsNull()
    {
        var result = await ProjectSession.TryReadAsync(
            Path.Combine(_projectPath, "Nonexistent.al"), CancellationToken.None);
        Assert.Null(result);
    }
}
