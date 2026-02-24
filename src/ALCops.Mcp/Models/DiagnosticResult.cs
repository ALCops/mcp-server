namespace ALCops.Mcp.Models;

public record DiagnosticResult(
    string Id,
    string Message,
    string Severity,
    string FilePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string CopName,
    bool HasCodeFix);
