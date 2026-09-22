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
    private ImmutableDictionary<string, TrackedDocument> _documents;
    private ImmutableDictionary<string, DocumentId> _filePathToDocumentId;

    internal AlProjectWorkspace Workspace { get; }
    public ProjectId ProjectId { get; }
    public string ProjectPath { get; }
    public ImmutableDictionary<string, DocumentId> FilePathToDocumentId => _filePathToDocumentId;

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
        try
        {
            var onDisk = new HashSet<string>(
                ProjectLoader.EnumerateAlFiles(ProjectPath),
                StringComparer.OrdinalIgnoreCase);

            int updated = 0, added = 0, removed = 0;
            var newDocs = _documents.ToBuilder();

            // Tracked but not on disk → remove
            foreach (var (path, tracked) in _documents)
            {
                if (onDisk.Contains(path))
                    continue;

                try
                {
                    await Workspace.RemoveDocumentAsync(tracked.Id);
                }
                catch (IOException)
                {
                    continue;
                }

                newDocs.Remove(path);
                removed++;
            }

            // On disk: added or potentially updated
            foreach (var path in onDisk)
            {
                ct.ThrowIfCancellationRequested();

                if (_documents.TryGetValue(path, out var tracked))
                {
                    FileInfo fi;
                    try { fi = new FileInfo(path); }
                    catch (IOException) { continue; }

                    var stamp = FileStamp.Of(fi);
                    if (stamp == tracked.Stamp)
                        continue;

                    string newText;
                    try { newText = await File.ReadAllTextAsync(path, ct); }
                    catch (IOException) { continue; }

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
                    newDocs[path] = tracked with { Stamp = stamp };
                }
                else
                {
                    FileInfo fi;
                    try { fi = new FileInfo(path); }
                    catch (IOException) { continue; }

                    var stamp = FileStamp.Of(fi);

                    string content;
                    try { content = await File.ReadAllTextAsync(path, ct); }
                    catch (IOException) { continue; }

                    var docInfo = ProjectLoader.CreateDocumentInfo(ProjectId, path, content);

                    try
                    {
                        await Workspace.AddDocumentAsync(docInfo);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    newDocs[path] = new TrackedDocument(docInfo.Id, stamp);
                    added++;
                }
            }

            var summary = new RefreshSummary(updated, added, removed);

            if (summary.Any)
            {
                _documents = newDocs.ToImmutable();
                _filePathToDocumentId = BuildDocumentIdMap(_documents);
                Console.Error.WriteLine(
                    $"Refreshed {ProjectPath}: {updated} updated, {added} added, {removed} removed .al file(s).");
            }

            return summary;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        Workspace.Dispose();
        _gate.Dispose();
    }

    private static ImmutableDictionary<string, DocumentId> BuildDocumentIdMap(
        ImmutableDictionary<string, TrackedDocument> documents) =>
        documents.ToImmutableDictionary(
            kv => kv.Key, kv => kv.Value.Id, StringComparer.OrdinalIgnoreCase);
}
