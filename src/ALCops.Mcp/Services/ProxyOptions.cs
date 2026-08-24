namespace ALCops.Mcp.Services;

/// <summary>
/// The server's own CLI surface, plus the args it forwards verbatim to the child <c>almcp</c>.
/// </summary>
public sealed record ProxyOptions(
    bool ProxyDisabled,
    string? DevToolsPath,
    string[]? Projects,
    string[] PassthroughArgs)
{
    // almcp's arg parsing (ALMcpOptions.ParseArguments) has three arities, and treating them all
    // alike silently drops flags or swallows the following one as a value:
    //   - pure booleans never consume a value
    //   - optional booleans consume the next arg only when it is literally "true"/"false"
    //   - value flags always consume the next arg
    private static readonly HashSet<string> BooleanFlags = ["--nolog", "--debug"];

    private static readonly HashSet<string> OptionalBooleanFlags =
        ["--noauth", "--enablecodeanalysis", "--enableexternalrulesets"];

    private static readonly HashSet<string> ValueFlags =
    [
        "--codeanalyzers", "--rulesetpath", "--settingspath", "--locale",
        "--packagecachepath", "--assemblyprobingpaths", "--outfolder",
        "--logfile", "--loglevel", "--mode", "--environment",
    ];

    public static ProxyOptions Parse(string[] args)
    {
        bool disabled = false;
        string? devToolsPath = null;
        string[]? projects = null;
        var passthroughArgs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var flag = args[i].ToLowerInvariant();

            switch (flag)
            {
                case "--no-proxy":
                    disabled = true;
                    continue;

                case "--devtools-path" when i + 1 < args.Length:
                    devToolsPath = args[++i];
                    continue;

                case "--projects" when i + 1 < args.Length:
                    projects = args[++i].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    continue;
            }

            if (BooleanFlags.Contains(flag))
            {
                passthroughArgs.Add(args[i]);
            }
            else if (OptionalBooleanFlags.Contains(flag))
            {
                passthroughArgs.Add(args[i]);
                if (i + 1 < args.Length && args[i + 1] is "true" or "false")
                    passthroughArgs.Add(args[++i]);
            }
            else if (ValueFlags.Contains(flag) && i + 1 < args.Length)
            {
                passthroughArgs.Add(args[i]);
                passthroughArgs.Add(args[++i]);
            }
        }

        return new ProxyOptions(disabled, devToolsPath, projects, [.. passthroughArgs]);
    }
}
