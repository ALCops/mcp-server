using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ALCops.Mcp.Services;

/// <summary>
/// Success results of the native tools. Errors go through <see cref="ToolErrors"/> only.
/// </summary>
internal static class ToolResults
{
    /// <summary>
    /// The payload serialized with <see cref="JsonDefaults.Options"/> as a single text block.
    /// <see cref="CallToolResult.IsError"/> is left unset: only errors set it.
    /// </summary>
    internal static CallToolResult Ok<T>(T payload) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, JsonDefaults.Options) }]
    };
}
