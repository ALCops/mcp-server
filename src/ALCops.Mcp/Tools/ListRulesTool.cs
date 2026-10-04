using System.ComponentModel;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Tools;

[McpServerToolType]
public sealed class ListRulesTool
{
    [McpServerTool(Name = "list_rules", ReadOnly = true),
     Description("List available analyzer rules. Rules come from the analyzers the project configures via al.codeAnalyzers — nothing is bundled. By default returns a compact list (ID, title, cop name). Use verbose=true for full metadata including description, severity, category, help URI, and code fix availability.")]
    public static async Task<CallToolResult> ListRules(
        ProjectAnalyzerResolver analyzerResolver,
        WorkspaceStartupResolver workspaceResolver,
        [Description("Optional: absolute path to one of the AL project folders this server was started with. Defaults to the project discovered at startup.")] string? projectPath = null,
        [Description("Filter rules by cop name (e.g., 'LinterCop', 'ApplicationCop', 'CodeCop'). Leave empty for all cops.")] string? copFilter = null,
        [Description("Optional: JSON array of analyzer specs (e.g., '[\"${CodeCop}\",\"${UICop}\"]'). If omitted, auto-discovers from .vscode/settings.json.")] string? analyzers = null,
        [Description("Return full rule metadata (description, severity, category, helpUri, hasCodeFix). Default: false.")] bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // No projectPath: fall back to the project discovered at startup, the same one the proxied
            // MS tools operate on. There is no project-independent rule list any more — analyzers are
            // whatever the project configures. An explicit projectPath must be one of the startup
            // projects, exactly as for analyze.
            var project = ProjectScope.Resolve(workspaceResolver.Config, projectPath, out var invalidMessage);
            if (project is null)
                return ToolErrors.Invalid(invalidMessage!);

            var analyzerSpecs = AnalyzerSpec.ParseJsonArray(analyzers);
            var provider = await analyzerResolver.ResolveAsync(project, analyzerSpecs, cancellationToken);
            var warnings = provider.Warnings.Count > 0 ? provider.Warnings : null;

            var descriptors = provider.GetAllDescriptors();

            var filtered = descriptors.Values
                .Select(d => (Descriptor: d, CopName: provider.GetCopName(d.Id)))
                .Where(r => copFilter is null || r.CopName.Equals(copFilter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Descriptor.Id);

            if (verbose)
            {
                var rules = filtered.Select(r => new RuleInfo(
                    Id: r.Descriptor.Id,
                    Title: r.Descriptor.Title.ToString(),
                    Description: r.Descriptor.Description.ToString(),
                    Severity: r.Descriptor.DefaultSeverity.ToString(),
                    Category: r.Descriptor.Category,
                    CopName: r.CopName,
                    HasCodeFix: provider.HasCodeFix(r.Descriptor.Id),
                    HelpUri: r.Descriptor.HelpLinkUri)).ToList();
                return ToolResults.Ok(new { rules, warnings });
            }
            else
            {
                var rules = filtered.Select(r => new
                {
                    id = r.Descriptor.Id,
                    title = r.Descriptor.Title.ToString(),
                    cop = r.CopName
                }).ToList();
                return ToolResults.Ok(new { rules, warnings });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolErrors.Faulted(ex);
        }
    }
}
