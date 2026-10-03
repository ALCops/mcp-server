namespace ALCops.Mcp.Models;

/// <summary>A file that a guarded write did not change (or restored), and why.</summary>
/// <param name="Kind">
/// <c>StaleFile</c> (changed on disk or deleted), <c>UnsupportedEncoding</c> (the file is not valid in its detected encoding, or the fixed text cannot be encoded in it),
/// <c>ReadFailed</c> (could not be read for the stale check), <c>WriteFailed</c> (the file whose commit threw),
/// <c>RolledBack</c> (committed, then restored) or <c>NotWritten</c> (staged but never attempted).
/// Serialized as <c>kind</c>; this vocabulary is additive and may be renamed by issue #41.
/// </param>
public record FileWriteConflict(string FilePath, string Message, string Kind = FileWriteConflictKind.StaleFile);

/// <summary>The values of <see cref="FileWriteConflict.Kind"/>.</summary>
public static class FileWriteConflictKind
{
    public const string StaleFile = "StaleFile";
    public const string UnsupportedEncoding = "UnsupportedEncoding";
    public const string ReadFailed = "ReadFailed";
    public const string WriteFailed = "WriteFailed";
    public const string RolledBack = "RolledBack";
    public const string NotWritten = "NotWritten";
}
