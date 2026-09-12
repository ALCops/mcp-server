using System.ComponentModel;
using System.Text.Json;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Tools;

[McpServerToolType]
public sealed class ListRulesTool
{
    [McpServerTool(Name = "list_rules", ReadOnly = true),
     Description("List available analyzer rules. Rules come from the analyzers the project configures via al.codeAnalyzers — nothing is bundled. By default returns a compact list (ID, title, cop name). Use verbose=true for full metadata including description, severity, category, help URI, and code fix availability.")]
    public static async Task<string> ListRules(
        ProjectAnalyzerResolver analyzerResolver,
        WorkspaceStartupResolver workspaceResolver,
        [Description("Optional: absolute path to the AL project folder. Defaults to the project discovered at startup.")] string? projectPath = null,
        [Description("Filter rules by cop name (e.g., 'LinterCop', 'ApplicationCop', 'CodeCop'). Leave empty for all cops.")] string? copFilter = null,
        [Description("Optional: JSON array of analyzer specs (e.g., '[\"${CodeCop}\",\"${UICop}\"]'). If omitted, auto-discovers from .vscode/settings.json.")] string? analyzers = null,
        [Description("Return full rule metadata (description, severity, category, helpUri, hasCodeFix). Default: false.")] bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        // No projectPath: fall back to the project discovered at startup, the same one the proxied
        // MS tools operate on. There is no project-independent rule list any more — analyzers are
        // whatever the project configures.
        projectPath ??= workspaceResolver.Config.PrimaryProject;
        if (projectPath is null)
            return JsonSerializer.Serialize(new
            {
                error = "NoProject",
                message = "No AL project available. Pass projectPath, or start the server from a folder " +
                    "containing app.json (or use --projects)."
            }, JsonDefaults.Options);

        var analyzerSpecs = AnalyzerSpec.ParseJsonArray(analyzers);
        var provider = await analyzerResolver.ResolveAsync(projectPath, analyzerSpecs, cancellationToken);
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
            return JsonSerializer.Serialize(new { rules, warnings }, JsonDefaults.Options);
        }
        else
        {
            var rules = filtered.Select(r => new
            {
                id = r.Descriptor.Id,
                title = r.Descriptor.Title.ToString(),
                cop = r.CopName
            }).ToList();
            return JsonSerializer.Serialize(new { rules, warnings }, JsonDefaults.Options);
        }
    }
}
