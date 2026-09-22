using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

public class GuardedFileWriterTests : IDisposable
{
    private readonly string _tempDir;

    public GuardedFileWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"alcops-guard-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestAnalyzers.TryDeleteDirectory(_tempDir);

    [Fact]
    public async Task MatchingFile_WritesAndReturnsNull()
    {
        var path = Path.Combine(_tempDir, "test.al");
        await File.WriteAllTextAsync(path, "original content");

        var conflict = await GuardedFileWriter.WriteIfUnchangedAsync(
            path, "original content", "new content", CancellationToken.None);

        Assert.Null(conflict);
        Assert.Equal("new content", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DifferingFile_ReturnsConflictAndFileUntouched()
    {
        var path = Path.Combine(_tempDir, "test.al");
        await File.WriteAllTextAsync(path, "someone else's edit");

        var conflict = await GuardedFileWriter.WriteIfUnchangedAsync(
            path, "original content", "new content", CancellationToken.None);

        Assert.NotNull(conflict);
        Assert.Equal(path, conflict!.FilePath);
        Assert.Contains("changed on disk", conflict.Message);
        Assert.Equal("someone else's edit", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task MissingFile_ReturnsConflict()
    {
        var path = Path.Combine(_tempDir, "deleted.al");

        var conflict = await GuardedFileWriter.WriteIfUnchangedAsync(
            path, "original content", "new content", CancellationToken.None);

        Assert.NotNull(conflict);
        Assert.Equal(path, conflict!.FilePath);
        Assert.Contains("deleted", conflict.Message);
    }
}
