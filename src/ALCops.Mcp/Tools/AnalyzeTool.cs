using System.ComponentModel;
using System.Text.Json;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Tools;

[McpServerToolType]
public sealed class AnalyzeTool
{
    public const int DefaultLimit = 500;

    [McpServerTool(Name = "analyze", ReadOnly = true),
     Description("Compile the AL workspace with all configured analyzers and return cop + compiler diagnostics as structured JSON. Wraps Microsoft's al_compile (onlyErrors=false, enableCodeAnalysis=true, no diagnostic cap) using the analyzers and ruleset the server passed to almcp at startup, then enriches each diagnostic with the owning analyzer ('CodeCop', 'ALCops.LinterCop', ..., or 'Compiler' for AL#### errors) and whether a native code fix exists (hasFix). Prefer this over al_compile or al_getdiagnostics whenever you want cop diagnostics: al_compile hides warnings unless you remember onlyErrors=false, and al_getdiagnostics never runs analyzers. Scope with filePath, folderPath or projectPath (combined with AND); filter with severities, analyzers, ruleIds; cap with limit (default 500). totalCount, truncated and summary always describe the full filtered set. Results are sorted by filePath, line, column. Scoping: without any scope argument, results are limited to the startup project. With filePath or folderPath and no projectPath, the file/folder is the only scope and analyzer/hasFix come from the analyzer configuration of the project that contains it (falling back to the startup project). The 'project' field names that project. Next steps: for a diagnostic with hasFix=true call get_fixes (then apply_fix) at its filePath/line/column/id, or apply_fix_all for every occurrence of one rule. After apply_fix / apply_fix_all, call analyze again to verify; almcp's file watcher normally sees the write first, but on slow file systems or right after a large apply_fix_all a second call may be needed before the fixed diagnostic disappears.")]
    public static async Task<string> Analyze(
        IServiceProvider services,
        ProjectAnalyzerResolver analyzerResolver,
        WorkspaceStartupResolver workspaceResolver,
        [Description("Optional: absolute path to a single .al file. Only diagnostics in that file are returned.")] string? filePath = null,
        [Description("Optional: absolute folder path. Only diagnostics in files under it (recursively) are returned.")] string? folderPath = null,
        [Description("Optional: absolute path to the AL project folder (contains app.json). Scopes results to that project and selects whose analyzer set is used for 'analyzer'/'hasFix'. Defaults to the project discovered at startup.")] string? projectPath = null,
        [Description("Optional: keep only these severities, e.g. [\"Error\",\"Warning\"]. Case-insensitive. Values: Error, Warning, Info, Hidden.")] string[]? severities = null,
        [Description("Optional: keep only diagnostics from these analyzers, by the cop name list_rules reports (e.g. \"CodeCop\", \"ALCops.LinterCop\") or \"Compiler\" for AL#### compiler diagnostics. Case-insensitive.")] string[]? analyzers = null,
        [Description("Optional: keep only these rule IDs, e.g. [\"LC0020\",\"AL0432\"]. Case-insensitive.")] string[]? ruleIds = null,
        [Description("Maximum diagnostics returned after filtering (default 500). totalCount and summary always cover the full filtered set.")] int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var proxy = services.GetService(typeof(AlMcpProxy)) as AlMcpProxy;
            if (proxy is null)
                return Error("ProxyUnavailable",
                    "analyze wraps the proxied al_compile, but this server runs with --no-proxy. " +
                    "Restart without --no-proxy to use analyze.");

            if (!proxy.IsAvailable || !await proxy.Ready.WaitAsync(cancellationToken))
                return Error("ProxyUnavailable",
                    "MS AL MCP Server (almcp) is not available (not found in the DevTools directory, or it failed to start). " +
                    "See the server log on stderr.");

            var callerPassedProjectPath = projectPath is not null;
            var callerPassedFileOrFolder = filePath is not null || folderPath is not null;
            var callerPassedAnyScope = callerPassedProjectPath || callerPassedFileOrFolder;

            var normalizedFilePath = filePath is not null ? Path.GetFullPath(filePath) : null;
            var normalizedFolderPath = folderPath is not null ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath)) : null;

            var scopePath = normalizedFilePath ?? normalizedFolderPath;
            string? enrichmentProject;
            if (callerPassedProjectPath)
            {
                enrichmentProject = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath!));

                var knownProjects = workspaceResolver.Config.ProjectDirectories
                    .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).ToList();
                if (!knownProjects.Contains(enrichmentProject, StringComparer.OrdinalIgnoreCase))
                    return Error("UnknownProject",
                        $"'{projectPath}' is not one of the AL projects this server was started with: " +
                        $"{string.Join(", ", knownProjects)}. Pass one of those, or restart the server with --projects.");
            }
            else if (scopePath is not null)
            {
                enrichmentProject = CompileDiagnosticsParser.FindContainingProject(
                    scopePath, workspaceResolver.Config.ProjectDirectories)
                    ?? workspaceResolver.Config.PrimaryProject;
            }
            else
            {
                enrichmentProject = workspaceResolver.Config.PrimaryProject;
            }

            if (enrichmentProject is null)
                return Error("NoProject",
                    "No AL project available. Pass projectPath, or start the server from a folder " +
                    "containing app.json (or use --projects).");

            // Project filter applies when projectPath is explicit or when no file/folder scope was given.
            var projectScopeFilter = callerPassedFileOrFolder && !callerPassedProjectPath ? null : enrichmentProject;

            if (limit <= 0)
                return Error("InvalidLimit", "limit must be a positive integer.");

            HashSet<string>? severitySet = severities is { Length: > 0 }
                ? new HashSet<string>(severities, StringComparer.OrdinalIgnoreCase) : null;
            HashSet<string>? analyzerSet = analyzers is { Length: > 0 }
                ? new HashSet<string>(analyzers, StringComparer.OrdinalIgnoreCase) : null;
            HashSet<string>? ruleIdSet = ruleIds is { Length: > 0 }
                ? new HashSet<string>(ruleIds, StringComparer.OrdinalIgnoreCase) : null;

            var resolvedAnalyzers = await analyzerResolver.ResolveAsync(enrichmentProject, null, cancellationToken);
            var warnings = new List<string>(resolvedAnalyzers.Warnings);

            var args = new Dictionary<string, JsonElement>
            {
                ["options"] = JsonSerializer.SerializeToElement(new
                {
                    onlyErrors = false,
                    enableCodeAnalysis = true,
                    maxDiagnosticsPerCompilation = int.MaxValue
                }, JsonDefaults.Options)
            };

            var result = await proxy.ForwardAsync("al_compile", args, cancellationToken);

            if (result.IsError == true)
            {
                var errorMessage = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));
                return Error("ProxyCallFailed",
                    "The proxied al_compile call failed (almcp may have exited, or its session was lost): " + errorMessage);
            }

            var (raw, message, succeeded) = CompileDiagnosticsParser.Parse(result);
            warnings.AddRange(CompileDiagnosticsParser.ExtractWarnings(message));

            var enriched = CompileDiagnosticsParser.Enrich(raw, resolvedAnalyzers.GetCopName, resolvedAnalyzers.HasCodeFix, Path.GetFullPath);
            var includeUnlocated = !callerPassedAnyScope;
            var filter = new AnalyzeFilter(normalizedFilePath, normalizedFolderPath, projectScopeFilter, includeUnlocated, severitySet, analyzerSet, ruleIdSet);
            var (filtered, droppedUnlocated) = CompileDiagnosticsParser.Filter(enriched, filter);
            var sorted = CompileDiagnosticsParser.Sort(filtered);

            var compileWarnings = CompileDiagnosticsParser.BuildWarnings(succeeded, raw.Count, filtered.Count, droppedUnlocated);
            warnings.InsertRange(0, compileWarnings);

            var analyzeResult = CompileDiagnosticsParser.Build(enrichmentProject, sorted, limit, warnings);

            return JsonSerializer.Serialize(analyzeResult, JsonDefaults.Options);
        }
        catch (Exception ex)
        {
            return Error(ex.GetType().Name, ex.Message);
        }
    }

    private static string Error(string code, string message) =>
        JsonSerializer.Serialize(new { error = code, message }, JsonDefaults.Options);
}
