using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>Assertions over the <see cref="CallToolResult"/> the native tools return.</summary>
internal static class ToolResultAssert
{
    /// <summary>The single text block of a native tool result.</summary>
    public static string Text(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    /// <summary>Asserts a success result (no isError, no error property) and returns its JSON root.</summary>
    public static JsonElement Ok(CallToolResult result)
    {
        var text = Text(result);
        Assert.True(result.IsError != true, $"Expected success, got an error result: {text}");

        var root = JsonDocument.Parse(text).RootElement;
        if (root.ValueKind == JsonValueKind.Object)
            Assert.False(root.TryGetProperty("error", out _), $"Success result carries an error property: {text}");
        return root;
    }

    /// <summary>Asserts a success result and deserializes it with the server's JSON options.</summary>
    public static T OkAs<T>(CallToolResult result)
    {
        Ok(result);
        return JsonSerializer.Deserialize<T>(Text(result), JsonDefaults.Options)!;
    }

    /// <summary>
    /// Asserts an error result with isError set, <paramref name="code"/> as <c>error</c> and, when given,
    /// <paramref name="reason"/> as <c>reason</c>. Returns the JSON root.
    /// </summary>
    public static JsonElement Error(CallToolResult result, string code, string? reason = null)
    {
        var text = Text(result);
        Assert.True(result.IsError == true, $"Expected isError = true: {text}");

        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(code, root.GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("message").GetString()), $"Empty message: {text}");

        if (reason is not null)
            Assert.Equal(reason, root.GetProperty("reason").GetString());

        return root;
    }
}
