using System.Collections.Immutable;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Text;
using Microsoft.Dynamics.Nav.CodeAnalysis.Workspaces;

namespace ALCops.Mcp.Services;

internal readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc)
{
    public static FileStamp Of(FileInfo fi) => new(fi.Length, fi.LastWriteTimeUtc);
}

internal sealed record TrackedDocument(DocumentId Id, FileStamp Stamp);

public readonly record struct RefreshSummary(int Updated, int Added, int Removed)
{
    public bool Any => Updated + Added + Removed > 0;
}

/// <summary>
/// Holds a loaded AL project's workspace, compilation, and document mappings.
/// </summary>
public sealed class ProjectSession : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _disposed;
    private ImmutableDictionary<string, TrackedDocument> _documents;
    private ImmutableDictionary<string, DocumentId> _filePathToDocumentId;

    internal AlProjectWorkspace Workspace { get; }
    public ProjectId ProjectId { get; }
    public string ProjectPath { get; }
    public ImmutableDictionary<string, DocumentId> FilePathToDocumentId => _filePathToDocumentId;
    internal ImmutableDictionary<string, TrackedDocument> TrackedDocuments => _documents;

    internal ProjectSession(
        AlProjectWorkspace workspace,
        ProjectId projectId,
        string projectPath,
        ImmutableDictionary<string, TrackedDocument> documents)
    {
        Workspace = workspace;
        ProjectId = projectId;
        ProjectPath = projectPath;
        _documents = documents;
        _filePathToDocumentId = BuildDocumentIdMap(documents);
    }

    /// <summary>
    /// Gets the current project from the workspace's solution.
    /// </summary>
    public Project GetProject() => Workspace.CurrentSolution.GetProject(ProjectId)!;

    /// <summary>
    /// Gets the compilation for the project.
    /// </summary>
    public async ValueTask<Compilation> GetCompilationAsync(CancellationToken ct = default)
    {
        var compilation = await GetProject().GetCompilationAsync(ct);
        return compilation ?? throw new InvalidOperationException("Failed to get compilation for project.");
    }

    /// <summary>
    /// Gets a Document by file path.
    /// </summary>
    public Document? GetDocument(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        return _documents.TryGetValue(normalizedPath, out var tracked)
            ? Workspace.CurrentSolution.GetDocument(tracked.Id)
            : null;
    }

    /// <summary>
    /// Incrementally syncs the workspace with on-disk changes: only changed/added/removed
    /// documents are touched, so untouched documents keep their DocumentId and compilation state.
    /// Mutations go through Workspace.OnDocument* (like almcp's ProjectWatcher), never
    /// TryApplyChanges on a forked solution.
    /// </summary>
    public async Task<RefreshSummary> RefreshFromDiskAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        ImmutableDictionary<string, TrackedDocument>.Builder? newDocs = null;
        int updated = 0, added = 0, removed = 0;
        try
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ProjectSession));

            var onDisk = new HashSet<string>(
                ProjectLoader.EnumerateAlFiles(ProjectPath),
                StringComparer.OrdinalIgnoreCase);

            newDocs = _documents.ToBuilder();

            // Tracked but not on disk → remove (OnDocument* calls are in-memory only)
            foreach (var (path, tracked) in _documents)
            {
                if (onDisk.Contains(path))
                    continue;

                await Workspace.RemoveDocumentAsync(tracked.Id);
                newDocs.Remove(path);
                removed++;
            }

            // On disk: added or potentially updated
            foreach (var path in onDisk)
            {
                ct.ThrowIfCancellationRequested();

                if (_documents.TryGetValue(path, out var tracked))
                {
                    var stamp = TryStat(path);
                    if (stamp is null)
                        continue;

                    if (stamp.Value == tracked.Stamp)
                        continue;

                    var newText = await TryReadAsync(path, ct);
                    if (newText is null)
                        continue;

                    var doc = Workspace.CurrentSolution.GetDocument(tracked.Id);
                    if (doc is null)
                        continue;

                    var currentText = (await doc.GetTextAsync(ct)).ToString();
                    if (!string.Equals(currentText, newText, StringComparison.Ordinal))
                    {
                        await Workspace.UpdateDocumentTextAsync(tracked.Id, SourceText.From(newText));
                        updated++;
                    }

                    // An edit that keeps both length and mtime identical is missed (FAT-class 2s
                    // timestamps only); the content compare protects the other direction (touch /
                    // git checkout without content change keeps compilation state).
                    newDocs[path] = tracked with { Stamp = stamp.Value };
                }
                else
                {
                    var stamp = TryStat(path);
                    if (stamp is null)
                        continue;

                    var content = await TryReadAsync(path, ct);
                    if (content is null)
                        continue;

                    // OnDocument* calls are in-memory only
                    var docInfo = ProjectLoader.CreateDocumentInfo(ProjectId, path, content);
                    await Workspace.AddDocumentAsync(docInfo);

                    newDocs[path] = new TrackedDocument(docInfo.Id, stamp.Value);
                    added++;
                }
            }

            var summary = new RefreshSummary(updated, added, removed);

            if (summary.Any)
            {
                Console.Error.WriteLine(
                    $"Refreshed {ProjectPath}: {updated} updated, {added} added, {removed} removed .al file(s).");
            }

            return summary;
        }
        finally
        {
            if (newDocs is not null)
            {
                _documents = newDocs.ToImmutable();
                if (added + removed > 0)
                    _filePathToDocumentId = BuildDocumentIdMap(_documents);
            }

            try { _gate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_gate.Wait(TimeSpan.FromSeconds(5)))
        {
            _gate.Release();
            _gate.Dispose();
        }
        // Timed out: leak the gate rather than dispose it with a pending waiter.
        Workspace.Dispose();
    }

    internal static FileStamp? TryStat(string path)
    {
        try
        {
            return FileStamp.Of(new FileInfo(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: could not read {path} during refresh ({ex.GetType().Name}); skipped this round.");
            return null;
        }
    }

    internal static async Task<string?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: could not read {path} during refresh ({ex.GetType().Name}); skipped this round.");
            return null;
        }
    }

    private static ImmutableDictionary<string, DocumentId> BuildDocumentIdMap(
        ImmutableDictionary<string, TrackedDocument> documents) =>
        documents.ToImmutableDictionary(
            kv => kv.Key, kv => kv.Value.Id, StringComparer.OrdinalIgnoreCase);
}
