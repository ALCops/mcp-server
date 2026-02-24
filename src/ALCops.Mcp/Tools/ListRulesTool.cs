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
     Description("List available analyzer rules. By default returns a compact list (ID, title, cop name). Use verbose=true for full metadata including description, severity, category, help URI, and code fix availability.")]
    public static async Task<string> ListRules(
        AnalyzerRegistry registry,
        ProjectAnalyzerResolver analyzerResolver,
        [Description("Optional: absolute path to the AL project folder. When provided, includes rules from external analyzers (CodeCop, UICop, etc.) configured in .vscode/settings.json.")] string? projectPath = null,
        [Description("Filter rules by cop name (e.g., 'LinterCop', 'ApplicationCop', 'CodeCop'). Leave empty for all cops.")] string? copFilter = null,
        [Description("Optional: JSON array of analyzer specs (e.g., '[\"${CodeCop}\",\"${UICop}\"]'). If omitted and projectPath is set, auto-discovers from .vscode/settings.json.")] string? analyzers = null,
        [Description("Return full rule metadata (description, severity, category, helpUri, hasCodeFix). Default: false.")] bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        IAnalyzerProvider provider;
        IReadOnlyList<string>? warnings = null;

        if (projectPath is not null)
        {
            var analyzerSpecs = ParseAnalyzerSpecs(analyzers);
            var analyzerSet = await analyzerResolver.ResolveAsync(projectPath, analyzerSpecs, cancellationToken);
            provider = analyzerSet;
            warnings = analyzerSet.Warnings.Count > 0 ? analyzerSet.Warnings : null;
        }
        else
        {
            provider = registry;
        }

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

    private static IReadOnlyList<string>? ParseAnalyzerSpecs(string? analyzers)
    {
        if (analyzers is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<string>>(analyzers);
        }
        catch
        {
            return null;
        }
    }
}
