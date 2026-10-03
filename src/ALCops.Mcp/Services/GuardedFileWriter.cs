using System.Text;
using ALCops.Mcp.Models;

namespace ALCops.Mcp.Services;

/// <summary>
/// The only code that writes <c>.al</c> files. Every write is guarded against stale content
/// (the file must still decode to the text the fix was computed from), atomic, and
/// encoding-preserving.
/// </summary>
/// <remarks>
/// <para><b>Atomicity.</b> New bytes go to a sibling temp file <c>&lt;path&gt;.&lt;guid&gt;.alcops.tmp</c>
/// in the same directory, which is then moved over the target with
/// <c>File.Move(temp, path, overwrite: true)</c>. A crash leaves either the old or the new file,
/// never a truncated one; a stray temp file is swept by <see cref="ProjectLoader.SweepTempFiles"/>.
/// Neither our <c>*.al</c> enumeration nor almcp's <c>.al</c> extension check matches the temp name.</para>
/// <para><b>Why not <c>File.Replace</c>, and why no backup file.</b> almcp's <c>ProjectWatcher</c>
/// (<c>Filter = "*.al"</c>; its Renamed handler checks <c>EndsWith(".al")</c> on the old and new
/// name) sees a move-over as an in-place update of the target and keeps its <c>DocumentId</c>.
/// <c>File.Replace(temp, path, backup)</c> produces two renames, so the watcher removes the
/// document and re-adds it under a new <c>DocumentId</c>, and the document is briefly missing.
/// A backup file would also be litter in the user's source tree. Batch rollback therefore restores
/// the original bytes held in memory, through the same temp + move path.</para>
/// <para><b>Encoding.</b> The file is read as bytes for the stale check and decoded with BOM
/// detection; the encoding of that read is what is written back (preamble + text). UTF-8 with or
/// without BOM and UTF-16/32 with BOM round-trip; line endings are whatever the new text holds.
/// The comparison is on decoded text, so a BOM never causes a false stale conflict.</para>
/// <para>The stale-check → move window is best-effort; a concurrent writer can still slip in.</para>
/// </remarks>
public sealed class GuardedFileWriter
{
    internal const string TempSuffix = ".alcops.tmp";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly Action<string, string> _move; // (temp, target)

    public GuardedFileWriter() : this(static (temp, target) => File.Move(temp, target, overwrite: true)) { }

    /// <summary>Test seam: replaces the commit move so failures can be injected cross-platform.</summary>
    internal GuardedFileWriter(Action<string, string> move) => _move = move;

    internal static string TempPathFor(string filePath) => $"{filePath}.{Guid.NewGuid():N}{TempSuffix}";

    /// <summary>
    /// Writes <paramref name="newContent"/> to <paramref name="filePath"/> if the file still holds
    /// <paramref name="expectedOriginal"/>; otherwise returns the conflict and writes nothing.
    /// An I/O failure throws, with the target untouched and no temp file left behind.
    /// </summary>
    public async Task<FileWriteConflict?> WriteIfUnchangedAsync(
        string filePath, string expectedOriginal, string newContent, CancellationToken ct)
    {
        var (staged, conflict) = await StageAsync(new PendingWrite(filePath, expectedOriginal, newContent), ct);
        if (conflict is not null)
            return conflict;

        await CommitAsync(staged!.FilePath, Encode(staged.NewContent, staged.Encoding), ct);
        return null;
    }

    /// <summary>
    /// Writes a batch in two phases. Phase 1 reads every file; changed or deleted ones drop out as
    /// <see cref="BatchWriteResult.StaleConflicts"/> and the rest proceed. Phase 2 commits them one
    /// by one; if a commit fails, every file already committed in this call is restored to its
    /// original bytes and the whole staged set is reported in <see cref="BatchWriteResult.RolledBack"/>.
    /// A cancellation during phase 2 rolls back the same way and then rethrows.
    /// </summary>
    public async Task<BatchWriteResult> WriteAllIfUnchangedAsync(IReadOnlyList<PendingWrite> writes, CancellationToken ct)
    {
        var staleConflicts = new List<FileWriteConflict>();
        var staged = new List<Staged>();

        foreach (var write in writes)
        {
            var (s, conflict) = await StageAsync(write, ct);
            if (conflict is not null)
                staleConflicts.Add(conflict);
            else
                staged.Add(s!);
        }

        var committed = new List<Staged>();
        foreach (var s in staged)
        {
            try
            {
                await CommitAsync(s.FilePath, Encode(s.NewContent, s.Encoding), ct);
                committed.Add(s);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                var result = await RollBackAsync(staged, committed, s, ex, staleConflicts);
                if (ex is OperationCanceledException)
                    throw;
                return result;
            }
        }

        return new BatchWriteResult([.. committed.Select(c => c.FilePath)], staleConflicts, [], null);
    }

    private async Task<BatchWriteResult> RollBackAsync(
        List<Staged> staged, List<Staged> committed, Staged failed, Exception failure,
        List<FileWriteConflict> staleConflicts)
    {
        var unrestored = new List<(string Path, Exception Error)>();
        foreach (var c in committed)
        {
            try
            {
                // Not cancellable: the caller's cancellation must not leave the batch half-applied.
                await CommitAsync(c.FilePath, c.OriginalBytes, CancellationToken.None);
            }
            catch (Exception ex)
            {
                unrestored.Add((c.FilePath, ex));
            }
        }

        var message = new StringBuilder(
            $"{failed.FilePath} could not be written ({failure.GetType().Name}: {failure.Message})");

        if (committed.Count == 0)
        {
            message.Append("; nothing from the batch was written.");
        }
        else if (unrestored.Count == 0)
        {
            message.Append($"; the {committed.Count} file(s) already written in this call were restored and nothing from the batch was kept.");
        }
        else
        {
            message.Append($"; {committed.Count - unrestored.Count} of the {committed.Count} file(s) already written in this call were restored.");
            message.Append(" Rollback failed for: ");
            message.Append(string.Join("; ", unrestored.Select(u => $"{u.Path} ({u.Error.GetType().Name}: {u.Error.Message})")));
            message.Append(" — these files are left with the fix applied.");
        }

        var failureMessage = message.ToString();
        var unrestoredPaths = unrestored.Select(u => u.Path).ToHashSet(StringComparer.Ordinal);
        var committedPaths = committed.Select(c => c.FilePath).ToHashSet(StringComparer.Ordinal);

        var rolledBack = staged
            .Where(s => !unrestoredPaths.Contains(s.FilePath))
            .Select(s => new FileWriteConflict(s.FilePath,
                ReferenceEquals(s, failed) ? failureMessage
                : committedPaths.Contains(s.FilePath) ? $"{s.FilePath} was rolled back because {failed.FilePath} could not be written."
                : $"{s.FilePath} was not written because {failed.FilePath} could not be written."))
            .ToList();

        return new BatchWriteResult([.. unrestored.Select(u => u.Path)], staleConflicts, rolledBack, failureMessage);
    }

    private static async Task<(Staged? Staged, FileWriteConflict? Conflict)> StageAsync(PendingWrite write, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(write.FilePath, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (null, new FileWriteConflict(write.FilePath,
                $"{write.FilePath} was deleted after the fix was computed; nothing was written."));
        }

        var current = Decode(bytes, out var encoding);
        if (!string.Equals(current, write.ExpectedOriginal, StringComparison.Ordinal))
            return (null, new FileWriteConflict(write.FilePath,
                $"{write.FilePath} changed on disk after the fix was computed; not overwritten."));

        return (new Staged(write.FilePath, bytes, encoding, write.NewContent), null);
    }

    private async Task CommitAsync(string filePath, byte[] bytes, CancellationToken ct)
    {
        var temp = TempPathFor(filePath);
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            _move(temp, filePath); // not cancellable: once the temp is complete, commit it
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    internal static string Decode(byte[] bytes, out Encoding encoding)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd(); // CurrentEncoding is only final after reading
        encoding = reader.CurrentEncoding;
        return text;
    }

    internal static byte[] Encode(string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);
        if (preamble.Length == 0)
            return body;

        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    private sealed record Staged(string FilePath, byte[] OriginalBytes, Encoding Encoding, string NewContent);
}
