namespace ALCops.Mcp.Models;

public record CodeFixResult(
    string FilePath,
    string OriginalContent,
    string ModifiedContent,
    string FixTitle);
