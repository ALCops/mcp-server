using ALCops.Mcp.Models;

namespace ALCops.Mcp.Services;

internal static class GuardedFileWriter
{
    // The read→write window is best-effort; a concurrent writer can still slip in.
    public static async Task<FileWriteConflict?> WriteIfUnchangedAsync(
        string filePath, string expectedOriginal, string newContent, CancellationToken ct)
    {
        string currentContent;
        try
        {
            currentContent = await File.ReadAllTextAsync(filePath, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new FileWriteConflict(filePath,
                $"{filePath} was deleted after the fix was computed; nothing was written.");
        }

        if (!string.Equals(currentContent, expectedOriginal, StringComparison.Ordinal))
            return new FileWriteConflict(filePath,
                $"{filePath} changed on disk after the fix was computed; not overwritten.");

        await File.WriteAllTextAsync(filePath, newContent, ct);
        return null;
    }
}
