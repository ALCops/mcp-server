namespace ALCops.Mcp.Models;

public record RuleInfo(
    string Id,
    string Title,
    string Description,
    string Severity,
    string Category,
    string CopName,
    bool HasCodeFix,
    string? HelpUri);
