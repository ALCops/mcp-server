namespace ALCops.Mcp.Models;

public record CodeFixResult(
    string FilePath,
    string ModifiedContent,
    string FixTitle);
