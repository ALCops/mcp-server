using System.Collections.Immutable;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Workspaces;

namespace ALCops.Mcp.Services;

/// <summary>
/// Holds a loaded AL project's workspace, compilation, and document mappings.
/// </summary>
public sealed class ProjectSession : IDisposable
{
    internal AlProjectWorkspace Workspace { get; }
    public ProjectId ProjectId { get; }
    public string ProjectPath { get; }
    public ImmutableDictionary<string, DocumentId> FilePathToDocumentId { get; }

    internal ProjectSession(
        AlProjectWorkspace workspace,
        ProjectId projectId,
        string projectPath,
        ImmutableDictionary<string, DocumentId> filePathToDocumentId)
    {
        Workspace = workspace;
        ProjectId = projectId;
        ProjectPath = projectPath;
        FilePathToDocumentId = filePathToDocumentId;
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
        return FilePathToDocumentId.TryGetValue(normalizedPath, out var docId)
            ? Workspace.CurrentSolution.GetDocument(docId)
            : null;
    }

    public void Dispose() => Workspace.Dispose();
}
