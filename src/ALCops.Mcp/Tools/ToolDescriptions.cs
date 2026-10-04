namespace ALCops.Mcp.Tools;

/// <summary>
/// Text published in <c>tools/list</c> that clients and tests depend on: the native tools' titles and
/// the sentences that steer callers away from the proxied tools' pitfalls. Kept in one place so the
/// attributes and the contract tests cannot drift apart; the tests pin the sentences as literals, so
/// rewording one here is a deliberate test change.
/// </summary>
internal static class ToolDescriptions
{
    public const string AnalyzeTitle = "Analyze AL project";
    public const string ListRulesTitle = "List analyzer rules";
    public const string GetFixesTitle = "Get code fixes";
    public const string ApplyFixTitle = "Apply code fix";
    public const string ApplyFixAllTitle = "Apply fix to all occurrences";

    public const string AnalyzePreferOverAlCompile =
        "Prefer this over al_compile or al_getdiagnostics whenever you want cop diagnostics: " +
        "al_compile hides warnings unless you remember onlyErrors=false, and al_getdiagnostics never runs analyzers.";

    public const string VerifyAfterWrite = "Verify with analyze or al_compile (options.onlyErrors: false).";
}
