namespace ALCops.Mcp.Models;

/// <summary>
/// One code fix offered for a diagnostic: the shape of both the <c>fixes</c> of <c>get_fixes</c> and
/// the <c>candidates</c> of an error, so the two are identical by construction.
/// <see cref="EquivalenceKey"/> is the action's key, or <c>""</c> when the provider set none; pass it
/// back verbatim.
/// </summary>
public record CodeFixInfo(
    string EquivalenceKey,
    string Title,
    string ProviderName);
