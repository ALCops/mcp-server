namespace ALCops.Mcp.Models;

/// <summary>
/// One code fix offered for a diagnostic: the element type of both the <c>fixes</c> of <c>get_fixes</c> and
/// the <c>candidates</c> of an error. <c>candidates</c> holds one entry per distinct key (first wins);
/// <c>fixes</c> lists every registered action.
/// <see cref="EquivalenceKey"/> is the action's key, or <c>""</c> when the provider set none; pass it
/// back verbatim.
/// </summary>
public record CodeFixInfo(
    string EquivalenceKey,
    string Title,
    string ProviderName);
