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
/// The comparison is on decoded text, so a BOM never causes a false stale conflict.
/// Every detected encoding (UTF-8 without BOM included) is decoded strictly: a file holding bytes
/// that are invalid in that encoding (e.g. a Windows-1252 byte in a UTF-8 file, or a lone surrogate
/// in a UTF-16 file) is refused as <c>UnsupportedEncoding</c> rather than re-encoded with U+FFFD.</para>
/// <para><b>Symlinks and permissions.</b> <c>File.Move</c> replaces a symlink at the target path with a
/// regular file and does not preserve Unix mode bits; both are accepted and out of scope.</para>
/// <para>The stale-check → move window is best-effort; a concurrent writer can still slip in.</para>
/// </remarks>
public sealed class GuardedFileWriter
{
    internal const string TempSuffix = ".alcops.tmp";

    // Strict decoders for every detected encoding: invalid input must throw, not decode to U+FFFD and
    // be re-encoded. The BOM flags make GetPreamble() emit the same BOM the file was read with.
    private static readonly Encoding StrictUtf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16LE = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16BE = new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf32LE = new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true);
    private static readonly Encoding StrictUtf32BE = new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true);

    private readonly Action<string, string> _move; // (temp, target)

    public GuardedFileWriter() : this(static (temp, target) => File.Move(temp, target, overwrite: true)) { }

    /// <summary>Test seam: replaces the commit move so failures can be injected cross-platform.</summary>
    internal GuardedFileWriter(Action<string, string> move) => _move = move;

    internal static string TempPathFor(string filePath) => $"{filePath}.{Guid.NewGuid():N}{TempSuffix}";

    /// <summary>
    /// Writes <paramref name="newContent"/> to <paramref name="filePath"/> if the file still holds
    /// <paramref name="expectedOriginal"/>; otherwise returns the conflict and writes nothing.
    /// A failure to read the file is returned as a conflict too; a failure to write it throws, with the
    /// target untouched and no temp file left behind.
    /// </summary>
    public async Task<FileWriteConflict?> WriteIfUnchangedAsync(
        string filePath, string expectedOriginal, string newContent, CancellationToken ct)
    {
        var (staged, conflict) = await StageAsync(new PendingWrite(filePath, expectedOriginal, newContent), ct);
        if (conflict is not null)
            return conflict;

        await CommitAsync(staged!.FilePath, staged.NewBytes, ct);
        return null;
    }

    /// <summary>
    /// Writes a batch in two phases. Phase 1 reads every file; changed, deleted, unreadable or invalidly encoded ones drop out as
    /// <see cref="BatchWriteResult.StaleConflicts"/> and the rest proceed. Phase 2 commits them one
    /// by one; if a commit fails, every file already committed in this call is restored to its
    /// original bytes (unless it was modified after this call wrote it) and the staged set is reported in
    /// <see cref="BatchWriteResult.RolledBack"/>.
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
                await CommitAsync(s.FilePath, s.NewBytes, ct);
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
        var restoreFailed = new List<(string Path, Exception Error)>();
        var modifiedSince = new List<string>();
        foreach (var c in committed)
        {
            try
            {
                // Someone may have edited the file after we wrote it; restoring would destroy that edit.
                var current = await File.ReadAllBytesAsync(c.FilePath, CancellationToken.None);
                if (!current.AsSpan().SequenceEqual(c.NewBytes))
                {
                    modifiedSince.Add(c.FilePath);
                    continue;
                }

                // Not cancellable: the caller's cancellation must not leave the batch half-applied.
                await CommitAsync(c.FilePath, c.OriginalBytes, CancellationToken.None);
            }
            catch (Exception ex)
            {
                restoreFailed.Add((c.FilePath, ex));
            }
        }

        var unrestoredCount = restoreFailed.Count + modifiedSince.Count;
        var message = new StringBuilder(
            $"{failed.FilePath} could not be written ({failure.GetType().Name}: {failure.Message})");

        if (committed.Count == 0)
        {
            message.Append("; nothing from the batch was written.");
        }
        else if (unrestoredCount == 0)
        {
            message.Append($"; the {committed.Count} file(s) already written in this call were restored and nothing from the batch was kept.");
        }
        else
        {
            message.Append($"; {committed.Count - unrestoredCount} of the {committed.Count} file(s) already written in this call were restored.");
            if (restoreFailed.Count > 0)
            {
                message.Append(" Rollback failed for: ");
                message.Append(string.Join("; ", restoreFailed.Select(u => $"{u.Path} ({u.Error.GetType().Name}: {u.Error.Message})")));
                message.Append(" — these files are left with the fix applied.");
            }
            foreach (var path in modifiedSince)
                message.Append($" {path} was modified after this call wrote it; left as is.");
        }

        var failureMessage = message.ToString();
        var unrestoredPaths = restoreFailed.Select(u => u.Path).Concat(modifiedSince).ToHashSet(StringComparer.Ordinal);
        var committedPaths = committed.Select(c => c.FilePath).ToHashSet(StringComparer.Ordinal);

        var rolledBack = staged
            .Where(s => !unrestoredPaths.Contains(s.FilePath))
            .Select(s =>
                ReferenceEquals(s, failed)
                    ? new FileWriteConflict(s.FilePath, failureMessage, FileWriteConflictKind.WriteFailed)
                : committedPaths.Contains(s.FilePath)
                    ? new FileWriteConflict(s.FilePath,
                        $"{s.FilePath} was rolled back because {failed.FilePath} could not be written.",
                        FileWriteConflictKind.RolledBack)
                    : new FileWriteConflict(s.FilePath,
                        $"{s.FilePath} was not written because {failed.FilePath} could not be written.",
                        FileWriteConflictKind.NotWritten))
            .ToList();

        // Unrestored files stay in Written, in commit order.
        var written = committed.Where(c => unrestoredPaths.Contains(c.FilePath)).Select(c => c.FilePath).ToList();
        return new BatchWriteResult(written, staleConflicts, rolledBack, failureMessage);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, new FileWriteConflict(write.FilePath,
                $"{write.FilePath} could not be read ({ex.GetType().Name}: {ex.Message}); not overwritten.",
                FileWriteConflictKind.ReadFailed));
        }

        string current;
        Encoding encoding;
        try
        {
            current = Decode(bytes, out encoding);
        }
        catch (DecoderFallbackException)
        {
            return (null, new FileWriteConflict(write.FilePath,
                $"{write.FilePath} contains bytes that are not valid in its detected encoding ({DisplayName(DetectEncoding(bytes, out _))}; e.g. a Windows-1252 byte in a UTF-8 file); not overwritten to avoid re-encoding it.",
                FileWriteConflictKind.UnsupportedEncoding));
        }

        if (!string.Equals(current, write.ExpectedOriginal, StringComparison.Ordinal))
            return (null, new FileWriteConflict(write.FilePath,
                $"{write.FilePath} changed on disk after the fix was computed; not overwritten."));

        // Encoded here, in phase 1, so nothing can fail on encoding once commits have started.
        return (new Staged(write.FilePath, bytes, Encode(write.NewContent, encoding)), null);
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

    /// <summary>
    /// Decodes <paramref name="bytes"/> with the encoding named by its BOM (UTF-8 without BOM if none),
    /// stripping the BOM. Throws <see cref="DecoderFallbackException"/> on any invalid input.
    /// </summary>
    internal static string Decode(byte[] bytes, out Encoding encoding)
    {
        encoding = DetectEncoding(bytes, out var preambleLength);
        return encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
    }

    private static Encoding DetectEncoding(ReadOnlySpan<byte> bytes, out int preambleLength)
    {
        // UTF-32 LE (FF FE 00 00) must be checked before UTF-16 LE (FF FE).
        (Encoding encoding, preambleLength) = bytes switch
        {
            [0xFF, 0xFE, 0x00, 0x00, ..] => (StrictUtf32LE, 4),
            [0x00, 0x00, 0xFE, 0xFF, ..] => (StrictUtf32BE, 4),
            [0xEF, 0xBB, 0xBF, ..] => (StrictUtf8Bom, 3),
            [0xFF, 0xFE, ..] => (StrictUtf16LE, 2),
            [0xFE, 0xFF, ..] => (StrictUtf16BE, 2),
            _ => (StrictUtf8NoBom, 0),
        };
        return encoding;
    }

    private static string DisplayName(Encoding encoding) =>
        ReferenceEquals(encoding, StrictUtf8Bom) ? "UTF-8 with BOM"
        : ReferenceEquals(encoding, StrictUtf16LE) ? "UTF-16 LE"
        : ReferenceEquals(encoding, StrictUtf16BE) ? "UTF-16 BE"
        : ReferenceEquals(encoding, StrictUtf32LE) ? "UTF-32 LE"
        : ReferenceEquals(encoding, StrictUtf32BE) ? "UTF-32 BE"
        : "UTF-8";

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

    private sealed record Staged(string FilePath, byte[] OriginalBytes, byte[] NewBytes);
}
