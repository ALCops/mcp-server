using Microsoft.Dynamics.Nav.CodeAnalysis.Workspaces;
using Microsoft.Dynamics.Nav.CodeAnalysis.Workspaces.Host;

namespace ALCops.Mcp.Services;

/// <summary>
/// Minimal Workspace subclass for creating Document objects required by CodeFixProviders.
/// This workspace is not connected to an IDE — it simply holds a solution with projects and documents.
/// </summary>
internal sealed class AlProjectWorkspace : Workspace
{
    public AlProjectWorkspace()
        : base(HostServices.DefaultHost, "ALCops.Mcp")
    {
    }

    public override bool CanApplyChange(ApplyChangesKind feature) => true;

    /// <summary>
    /// Adds a project to the workspace and returns the resulting Project object.
    /// </summary>
    public Project AddProject(ProjectInfo projectInfo)
    {
        var newSolution = CurrentSolution.AddProject(projectInfo);
        if (!TryApplyChanges(newSolution))
            throw new InvalidOperationException("Failed to add project to workspace.");
        return CurrentSolution.GetProject(projectInfo.Id)!;
    }

    /// <summary>
    /// Adds a document to an existing project and returns the resulting Document object.
    /// </summary>
    public Document AddDocument(DocumentInfo documentInfo)
    {
        var newSolution = CurrentSolution.AddDocument(documentInfo);
        if (!TryApplyChanges(newSolution))
            throw new InvalidOperationException("Failed to add document to workspace.");
        return CurrentSolution.GetDocument(documentInfo.Id)!;
    }

    /// <summary>
    /// Replaces the current solution with an updated one (e.g., after applying a code fix).
    /// </summary>
    public bool ApplyChanges(Solution newSolution) => TryApplyChanges(newSolution);
}
