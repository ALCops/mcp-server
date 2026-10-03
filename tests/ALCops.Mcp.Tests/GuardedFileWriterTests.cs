using System.Text;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

public class GuardedFileWriterTests : IDisposable
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly string _tempDir;
    private readonly GuardedFileWriter _writer = new();

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

        var conflict = await _writer.WriteIfUnchangedAsync(
            path, "original content", "new content", CancellationToken.None);

        Assert.Null(conflict);
        Assert.Equal("new content", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DifferingFile_ReturnsConflictAndFileUntouched()
    {
        var path = Path.Combine(_tempDir, "test.al");
        await File.WriteAllTextAsync(path, "someone else's edit");

        var conflict = await _writer.WriteIfUnchangedAsync(
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

        var conflict = await _writer.WriteIfUnchangedAsync(
            path, "original content", "new content", CancellationToken.None);

        Assert.NotNull(conflict);
        Assert.Equal(path, conflict!.FilePath);
        Assert.Contains("deleted", conflict.Message);
    }

    [Fact]
    public async Task MissingParentDirectory_ReturnsConflict()
    {
        var path = Path.Combine(_tempDir, "gone", "sub", "test.al");

        var conflict = await _writer.WriteIfUnchangedAsync(
            path, "original content", "new content", CancellationToken.None);

        Assert.NotNull(conflict);
        Assert.Equal(path, conflict!.FilePath);
        Assert.Contains("deleted", conflict.Message);
    }

    // --- Encoding preservation -------------------------------------------------------------

    [Fact]
    public async Task Utf8Bom_Preserved()
    {
        var path = Path.Combine(_tempDir, "bom.al");
        await File.WriteAllBytesAsync(path, [.. Utf8Bom, .. Utf8NoBom.GetBytes("original")]);

        var conflict = await _writer.WriteIfUnchangedAsync(path, "original", "new ü", CancellationToken.None);

        Assert.Null(conflict);
        byte[] expected = [.. Utf8Bom, .. Utf8NoBom.GetBytes("new ü")];
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Utf8NoBom_StaysWithoutBom()
    {
        var path = Path.Combine(_tempDir, "nobom.al");
        await File.WriteAllBytesAsync(path, Utf8NoBom.GetBytes("original"));

        var conflict = await _writer.WriteIfUnchangedAsync(path, "original", "new ü", CancellationToken.None);

        Assert.Null(conflict);
        Assert.Equal(Utf8NoBom.GetBytes("new ü"), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public Task Utf16LE_Preserved() => AssertEncodingRoundTrips(new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

    [Fact]
    public Task Utf16BE_Preserved() => AssertEncodingRoundTrips(new UnicodeEncoding(bigEndian: true, byteOrderMark: true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Utf32_Preserved(bool bigEndian) => AssertEncodingRoundTrips(new UTF32Encoding(bigEndian, byteOrderMark: true));

    private async Task AssertEncodingRoundTrips(Encoding encoding)
    {
        var path = Path.Combine(_tempDir, "encoded.al");
        await File.WriteAllBytesAsync(path, [.. encoding.GetPreamble(), .. encoding.GetBytes("original")]);

        var conflict = await _writer.WriteIfUnchangedAsync(path, "original", "new ü", CancellationToken.None);

        Assert.Null(conflict);
        byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes("new ü")];
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Bom_DoesNotCauseStaleConflict()
    {
        // The session text never contains the BOM (it is stripped on read), so expectedOriginal
        // is BOM-less while the file on disk has one.
        var path = Path.Combine(_tempDir, "bom.al");
        await File.WriteAllBytesAsync(path, [.. Utf8Bom, .. Utf8NoBom.GetBytes("original")]);

        var conflict = await _writer.WriteIfUnchangedAsync(path, "original", "new", CancellationToken.None);

        Assert.Null(conflict);
        Assert.Equal("new", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public Task Crlf_RoundTrips() => AssertLineEndingsRoundTrip("\r\n");

    [Fact]
    public Task Lf_RoundTrips() => AssertLineEndingsRoundTrip("\n");

    private async Task AssertLineEndingsRoundTrip(string eol)
    {
        var path = Path.Combine(_tempDir, "eol.al");
        var original = $"line 1{eol}line 2{eol}";
        var updated = $"line 1{eol}line 2 fixed{eol}";
        await File.WriteAllBytesAsync(path, Utf8NoBom.GetBytes(original));

        var conflict = await _writer.WriteIfUnchangedAsync(path, original, updated, CancellationToken.None);

        Assert.Null(conflict);
        Assert.Equal(Utf8NoBom.GetBytes(updated), await File.ReadAllBytesAsync(path));
    }

    // --- Atomicity ---------------------------------------------------------------------------

    [Fact]
    public async Task NoTempFileLeftAfterSuccess()
    {
        var path = Path.Combine(_tempDir, "test.al");
        await File.WriteAllTextAsync(path, "original");

        await _writer.WriteIfUnchangedAsync(path, "original", "new", CancellationToken.None);

        Assert.Equal([path], Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task MoveFails_TargetUntouched_TempDeleted_Throws()
    {
        var path = Path.Combine(_tempDir, "test.al");
        byte[] originalBytes = [.. Utf8Bom, .. Utf8NoBom.GetBytes("original")];
        await File.WriteAllBytesAsync(path, originalBytes);

        var writer = new GuardedFileWriter((_, _) => throw new IOException("injected move failure"));

        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteIfUnchangedAsync(path, "original", "new", CancellationToken.None));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public void TempPathFor_IsSiblingWithSuffix_NotEnumeratedAsAl()
    {
        var path = Path.Combine(_tempDir, "Page.al");
        File.WriteAllText(path, "// page");

        var temp = GuardedFileWriter.TempPathFor(path);
        File.WriteAllText(temp, "// temp");

        Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(temp));
        Assert.StartsWith(path + ".", temp);
        Assert.EndsWith(GuardedFileWriter.TempSuffix, temp);
        Assert.NotEqual(".al", Path.GetExtension(temp));
        Assert.Equal([Path.GetFullPath(path)], ProjectLoader.EnumerateAlFiles(_tempDir));
    }

    // --- Batch -------------------------------------------------------------------------------

    [Fact]
    public async Task Batch_AllFresh_WritesAll()
    {
        var (a, b, c) = await CreateThreeFiles();

        var result = await _writer.WriteAllIfUnchangedAsync(
            [Write(a, "a", "A"), Write(b, "b", "B"), Write(c, "c", "C")], CancellationToken.None);

        Assert.Equal([a, b, c], result.Written);
        Assert.Empty(result.StaleConflicts);
        Assert.Empty(result.RolledBack);
        Assert.Null(result.FailureMessage);
        Assert.Equal("A", await File.ReadAllTextAsync(a));
        Assert.Equal("B", await File.ReadAllTextAsync(b));
        Assert.Equal("C", await File.ReadAllTextAsync(c));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Batch_OneStale_WritesOthers()
    {
        var (a, b, c) = await CreateThreeFiles();
        await File.WriteAllTextAsync(b, "edited elsewhere");

        var result = await _writer.WriteAllIfUnchangedAsync(
            [Write(a, "a", "A"), Write(b, "b", "B"), Write(c, "c", "C")], CancellationToken.None);

        Assert.Equal([a, c], result.Written);
        var stale = Assert.Single(result.StaleConflicts);
        Assert.Equal(b, stale.FilePath);
        Assert.Contains("changed on disk", stale.Message);
        Assert.Empty(result.RolledBack);
        Assert.Null(result.FailureMessage);
        Assert.Equal("edited elsewhere", await File.ReadAllTextAsync(b));
        Assert.Equal("A", await File.ReadAllTextAsync(a));
        Assert.Equal("C", await File.ReadAllTextAsync(c));
    }

    [Fact]
    public async Task Batch_SecondOfThreeCommitFails_RollsBackFirst_ReportsAll()
    {
        var (a, b, c) = await CreateThreeFiles();
        // File 1 carries a BOM to prove the rollback restores bytes, not just text.
        byte[] aOriginal = [.. Utf8Bom, .. Utf8NoBom.GetBytes("a")];
        await File.WriteAllBytesAsync(a, aOriginal);
        var bOriginal = await File.ReadAllBytesAsync(b);
        var cOriginal = await File.ReadAllBytesAsync(c);

        var writer = FailingOnCalls(2);

        var result = await writer.WriteAllIfUnchangedAsync(
            [Write(a, "a", "A"), Write(b, "b", "B"), Write(c, "c", "C")], CancellationToken.None);

        Assert.Empty(result.Written);
        Assert.Empty(result.StaleConflicts);
        Assert.Equal(3, result.RolledBack.Count);
        Assert.Equal([a, b, c], result.RolledBack.Select(r => r.FilePath));
        Assert.NotNull(result.FailureMessage);
        Assert.StartsWith($"{b} could not be written", result.FailureMessage);
        Assert.Contains("IOException", result.FailureMessage);
        Assert.Contains("restored", result.FailureMessage);
        Assert.Equal(result.FailureMessage, result.RolledBack[1].Message);

        Assert.Equal(aOriginal, await File.ReadAllBytesAsync(a));
        Assert.Equal(bOriginal, await File.ReadAllBytesAsync(b));
        Assert.Equal(cOriginal, await File.ReadAllBytesAsync(c));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Batch_RollbackMoveFails_ReportedInMessage_FileStaysWritten()
    {
        var (a, b, c) = await CreateThreeFiles();

        // Call 1 commits a, call 2 (b) fails, call 3 is a's rollback and fails too.
        var writer = FailingOnCalls(2, 3);

        var result = await writer.WriteAllIfUnchangedAsync(
            [Write(a, "a", "A"), Write(b, "b", "B"), Write(c, "c", "C")], CancellationToken.None);

        Assert.Equal([a], result.Written);
        Assert.Equal([b, c], result.RolledBack.Select(r => r.FilePath));
        Assert.NotNull(result.FailureMessage);
        Assert.Contains("Rollback failed for", result.FailureMessage);
        Assert.Contains(a, result.FailureMessage);
        Assert.Contains("left with the fix applied", result.FailureMessage);

        Assert.Equal("A", await File.ReadAllTextAsync(a));
        Assert.Equal("b", await File.ReadAllTextAsync(b));
        Assert.Equal("c", await File.ReadAllTextAsync(c));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Batch_StaleAndFailure_BothReported()
    {
        var (a, b, c) = await CreateThreeFiles();
        await File.WriteAllTextAsync(a, "edited elsewhere");

        // a drops out in phase 1; call 1 commits b, call 2 (c) fails, call 3 restores b.
        var writer = FailingOnCalls(2);

        var result = await writer.WriteAllIfUnchangedAsync(
            [Write(a, "a", "A"), Write(b, "b", "B"), Write(c, "c", "C")], CancellationToken.None);

        Assert.Empty(result.Written);
        Assert.Equal(a, Assert.Single(result.StaleConflicts).FilePath);
        Assert.Equal([b, c], result.RolledBack.Select(r => r.FilePath));
        Assert.NotNull(result.FailureMessage);
        Assert.StartsWith($"{c} could not be written", result.FailureMessage);

        Assert.Equal("edited elsewhere", await File.ReadAllTextAsync(a));
        Assert.Equal("b", await File.ReadAllTextAsync(b));
        Assert.Equal("c", await File.ReadAllTextAsync(c));
        Assert.Empty(TempFiles());
    }

    private async Task<(string A, string B, string C)> CreateThreeFiles()
    {
        var a = Path.Combine(_tempDir, "A.al");
        var b = Path.Combine(_tempDir, "B.al");
        var c = Path.Combine(_tempDir, "C.al");
        await File.WriteAllTextAsync(a, "a");
        await File.WriteAllTextAsync(b, "b");
        await File.WriteAllTextAsync(c, "c");
        return (a, b, c);
    }

    private static PendingWrite Write(string path, string expected, string updated) => new(path, expected, updated);

    /// <summary>A writer whose move throws an <see cref="IOException"/> on the given 1-based call numbers.</summary>
    internal static GuardedFileWriter FailingOnCalls(params int[] failingCalls)
    {
        var calls = 0;
        return new GuardedFileWriter((temp, target) =>
        {
            if (failingCalls.Contains(Interlocked.Increment(ref calls)))
                throw new IOException("injected move failure");
            File.Move(temp, target, overwrite: true);
        });
    }

    private string[] TempFiles() =>
        Directory.GetFiles(_tempDir, "*" + GuardedFileWriter.TempSuffix, SearchOption.AllDirectories);
}
