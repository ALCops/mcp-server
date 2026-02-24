using System.Text.Json;

namespace ALCops.Mcp.Services;

public sealed class ProjectAnalyzerResolver
{
    private readonly AnalyzerRegistry _builtInRegistry;
    private readonly ExternalAnalyzerLoader _loader;
    private readonly RulesetLoader _rulesetLoader;

    private static readonly HashSet<string> AlCopsAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALCops.ApplicationCop",
        "ALCops.DocumentationCop",
        "ALCops.FormattingCop",
        "ALCops.LinterCop",
        "ALCops.PlatformCop",
        "ALCops.TestAutomationCop",
        "ALCops.Analyzers"
    };

    public ProjectAnalyzerResolver(AnalyzerRegistry builtInRegistry, ExternalAnalyzerLoader loader, RulesetLoader rulesetLoader)
    {
        _builtInRegistry = builtInRegistry;
        _loader = loader;
        _rulesetLoader = rulesetLoader;
    }

    public async Task<AnalyzerSet> ResolveAsync(
        string projectPath,
        IReadOnlyList<string>? analyzerSpecs = null,
        CancellationToken ct = default)
    {
        var specs = analyzerSpecs ?? ReadAnalyzerSpecsFromSettings(projectPath);

        var loaded = new List<LoadedAnalyzerAssembly>();
        var warnings = new List<string>();

        if (specs is not null && specs.Count > 0)
        {
            foreach (var rawSpec in specs)
            {
                if (string.IsNullOrWhiteSpace(rawSpec))
                    continue;

                var spec = AnalyzerSpec.Parse(rawSpec);

                // Skip ALCops' own DLLs — they're already built-in
                if (IsAlCopsDll(spec))
                    continue;

                try
                {
                    var assembly = await _loader.ResolveAndLoadAsync(spec, projectPath, ct);
                    if (assembly is not null)
                        loaded.Add(assembly);
                    else
                        warnings.Add($"Could not load analyzer: {rawSpec}");
                }
                catch (Exception ex)
                {
                    warnings.Add($"Error loading analyzer '{rawSpec}': {ex.Message}");
                }
            }
        }

        // Load ruleset
        var ruleActions = await LoadRulesetAsync(projectPath);

        return new AnalyzerSet(_builtInRegistry, loaded, warnings, ruleActions);
    }

    private async Task<Dictionary<string, RuleAction>?> LoadRulesetAsync(string projectPath)
    {
        // 1. Check al.ruleSetPath in .vscode/settings.json
        var rulesetPath = ReadRulesetPathFromSettings(projectPath);

        // 2. Fallback: check .AL-Go/settings.json rulesetFile
        if (rulesetPath is null)
            rulesetPath = ReadRulesetPathFromAlGo(projectPath);

        // 3. Fallback: check for common ruleset file names at project/repo level
        if (rulesetPath is null)
        {
            var repoRoot = FindRepoRoot(projectPath);
            foreach (var candidate in new[] { projectPath, repoRoot })
            {
                if (candidate is null) continue;
                foreach (var name in new[] { "custom.ruleset.json", "app.ruleset.json", ".codeAnalysis/app.ruleset.json" })
                {
                    var fullPath = Path.Combine(candidate, name);
                    if (File.Exists(fullPath))
                    {
                        rulesetPath = fullPath;
                        break;
                    }
                }
                if (rulesetPath is not null) break;
            }
        }

        if (rulesetPath is null)
            return null;

        // Determine if external rulesets are enabled
        var enableExternal = ReadEnableExternalRulesets(projectPath);

        try
        {
            return await _rulesetLoader.LoadAsync(rulesetPath, enableExternal);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load ruleset {rulesetPath}: {ex.Message}");
            return null;
        }
    }

    private static string? ReadRulesetPathFromSettings(string projectPath)
    {
        var settingsPath = Path.Combine(projectPath, ".vscode", "settings.json");
        return ReadJsonStringProperty(settingsPath, "al.ruleSetPath", projectPath);
    }

    private static string? ReadRulesetPathFromAlGo(string projectPath)
    {
        // .AL-Go/settings.json is typically at the repo root, not the project subfolder
        var repoRoot = FindRepoRoot(projectPath);

        foreach (var root in new[] { repoRoot, projectPath })
        {
            if (root is null) continue;
            var alGoPath = Path.Combine(root, ".AL-Go", "settings.json");
            var rulesetFile = ReadJsonStringProperty(alGoPath, "rulesetFile", null);
            if (rulesetFile is not null)
            {
                // rulesetFile is relative to the repo root
                var fullPath = Path.IsPathRooted(rulesetFile)
                    ? rulesetFile
                    : Path.GetFullPath(Path.Combine(root, rulesetFile));
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        return null;
    }

    private static bool ReadEnableExternalRulesets(string projectPath)
    {
        // Default to true — most projects that use rulesets enable external
        var repoRoot = FindRepoRoot(projectPath);
        foreach (var root in new[] { repoRoot, projectPath })
        {
            if (root is null) continue;
            var alGoPath = Path.Combine(root, ".AL-Go", "settings.json");
            if (!File.Exists(alGoPath)) continue;
            try
            {
                var json = File.ReadAllText(alGoPath);
                using var doc = JsonDocument.Parse(json, JsonDocOptions);
                if (doc.RootElement.TryGetProperty("enableExternalRulesets", out var prop) && prop.ValueKind == JsonValueKind.True)
                    return true;
                if (prop.ValueKind == JsonValueKind.False)
                    return false;
            }
            catch { }
        }

        return true;
    }

    private static string? ReadJsonStringProperty(string filePath, string propertyName, string? resolveRelativeTo)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json, JsonDocOptions);
            if (!doc.RootElement.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
                return null;

            var value = prop.GetString();
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (resolveRelativeTo is not null && !Path.IsPathRooted(value))
                return Path.GetFullPath(Path.Combine(resolveRelativeTo, value));

            return value;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? ReadAnalyzerSpecsFromSettings(string projectPath)
    {
        var settingsPath = Path.Combine(projectPath, ".vscode", "settings.json");
        if (!File.Exists(settingsPath))
            return null;

        try
        {
            var json = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(json, JsonDocOptions);

            if (!doc.RootElement.TryGetProperty("al.codeAnalyzers", out var analyzersElement))
                return null;

            if (analyzersElement.ValueKind != JsonValueKind.Array)
                return null;

            var specs = new List<string>();
            foreach (var element in analyzersElement.EnumerateArray())
            {
                var value = element.GetString();
                if (value is not null)
                    specs.Add(value);
            }

            return specs.Count > 0 ? specs : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to read analyzer settings from {settingsPath}: {ex.Message}");
            return null;
        }
    }

    private static string? FindRepoRoot(string startPath)
    {
        var dir = startPath;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static bool IsAlCopsDll(AnalyzerSpec spec)
    {
        if (spec.Kind != AnalyzerSpecKind.DllPath && spec.Kind != AnalyzerSpecKind.AnalyzerFolderRelative)
            return false;

        var fileName = Path.GetFileNameWithoutExtension(spec.GetDllFileName());
        return AlCopsAssemblyNames.Contains(fileName);
    }

    private static readonly JsonDocumentOptions JsonDocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
