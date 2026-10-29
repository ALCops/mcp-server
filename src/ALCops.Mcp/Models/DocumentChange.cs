namespace ALCops.Mcp.Models;

/// <summary>One file a code fix changes: its full text before and after the fix.</summary>
public record DocumentChange(string FilePath, string OriginalContent, string ModifiedContent);
