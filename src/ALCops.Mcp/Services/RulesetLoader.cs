using System.Collections.Concurrent;
using System.Text.Json;

namespace ALCops.Mcp.Services;

public sealed class RulesetLoader
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ConcurrentDictionary<string, Dictionary<string, RuleAction>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads a ruleset and all its included rulesets, returning a merged map of diagnosticId → action.
    /// Later rules override earlier ones. Local rules override included rulesets.
    /// </summary>
    public async Task<Dictionary<string, RuleAction>> LoadAsync(string rulesetPath, bool enableExternalRulesets = true)
    {
        var fullPath = Path.GetFullPath(rulesetPath);
        if (_cache.TryGetValue(fullPath, out var cached))
            return cached;

        var result = new Dictionary<string, RuleAction>(StringComparer.OrdinalIgnoreCase);
        await LoadRecursiveAsync(fullPath, result, enableExternalRulesets, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _cache[fullPath] = result;
        return result;
    }

    private async Task LoadRecursiveAsync(
        string pathOrUrl,
        Dictionary<string, RuleAction> mergedRules,
        bool enableExternalRulesets,
        HashSet<string> visited)
    {
        if (!visited.Add(pathOrUrl))
            return; // Prevent circular includes

        RulesetDocument? doc;
        try
        {
            doc = await ParseRulesetAsync(pathOrUrl, enableExternalRulesets);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load ruleset {pathOrUrl}: {ex.Message}");
            return;
        }

        if (doc is null)
            return;

        // Apply generalAction as baseline (if specified)
        // generalAction is handled by callers when checking diagnostics —
        // we store it as a wildcard entry "*"
        if (doc.GeneralAction is not null && TryParseAction(doc.GeneralAction, out var generalAction))
        {
            mergedRules["*"] = generalAction;
        }

        // Process included rulesets first (they can be overridden by local rules)
        if (doc.IncludedRuleSets is not null)
        {
            foreach (var include in doc.IncludedRuleSets)
            {
                if (include.Path is null)
                    continue;

                var isExternal = include.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                              || include.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                if (isExternal && !enableExternalRulesets)
                {
                    Console.Error.WriteLine($"Warning: Skipping external ruleset {include.Path} (enableExternalRulesets is false)");
                    continue;
                }

                // Resolve relative paths against the current ruleset's directory
                var resolvedPath = isExternal
                    ? include.Path
                    : ResolveRelativePath(pathOrUrl, include.Path);

                await LoadRecursiveAsync(resolvedPath, mergedRules, enableExternalRulesets, visited);
            }
        }

        // Apply local rules (override included rulesets)
        if (doc.Rules is not null)
        {
            foreach (var rule in doc.Rules)
            {
                if (rule.Id is not null && rule.Action is not null && TryParseAction(rule.Action, out var action))
                {
                    mergedRules[rule.Id] = action;
                }
            }
        }
    }

    private static async Task<RulesetDocument?> ParseRulesetAsync(string pathOrUrl, bool enableExternalRulesets)
    {
        string json;

        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
         || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!enableExternalRulesets)
                return null;

            json = await HttpClient.GetStringAsync(pathOrUrl);
        }
        else
        {
            if (!File.Exists(pathOrUrl))
                return null;

            json = await File.ReadAllTextAsync(pathOrUrl);
        }

        return JsonSerializer.Deserialize<RulesetDocument>(json, RulesetJsonOptions);
    }

    private static string ResolveRelativePath(string basePath, string relativePath)
    {
        if (basePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
         || basePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Resolve relative to URL directory
            var baseUri = new Uri(basePath);
            return new Uri(baseUri, relativePath).AbsoluteUri;
        }

        var baseDir = Path.GetDirectoryName(basePath) ?? ".";
        return Path.GetFullPath(Path.Combine(baseDir, relativePath));
    }

    private static bool TryParseAction(string value, out RuleAction action)
    {
        action = value.ToLowerInvariant() switch
        {
            "error" => RuleAction.Error,
            "warning" => RuleAction.Warning,
            "info" => RuleAction.Info,
            "hidden" => RuleAction.Hidden,
            "none" => RuleAction.None,
            "default" => RuleAction.Default,
            _ => RuleAction.Default
        };
        return true;
    }

    private static readonly JsonSerializerOptions RulesetJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class RulesetDocument
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? GeneralAction { get; set; }
        public List<IncludedRuleSet>? IncludedRuleSets { get; set; }
        public List<RuleEntry>? Rules { get; set; }
    }

    private sealed class IncludedRuleSet
    {
        public string? Action { get; set; }
        public string? Path { get; set; }
    }

    private sealed class RuleEntry
    {
        public string? Id { get; set; }
        public string? Action { get; set; }
        public string? Justification { get; set; }
    }
}

public enum RuleAction
{
    Default,
    Error,
    Warning,
    Info,
    Hidden,
    None
}
