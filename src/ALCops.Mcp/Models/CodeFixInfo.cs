namespace ALCops.Mcp.Models;

public record CodeFixInfo(
    string Title,
    string EquivalenceKey,
    string DiagnosticId,
    string ProviderName);
