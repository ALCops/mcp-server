using System.Text.Json.Serialization;

namespace ALCops.Mcp.Models;

/// <summary>
/// The single error envelope every native tool returns (with <c>isError: true</c>). Only
/// <see cref="Error"/> and <see cref="Message"/> are always present; the optional fields are omitted
/// when null. The parameter order is the JSON key order.
/// </summary>
public sealed record ToolError(
    string Error,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CodeFixInfo>? Candidates = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FilePath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DiagnosticId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null);

/// <summary>The fixed vocabulary of <see cref="ToolError.Error"/>.</summary>
public static class ToolErrorCode
{
    /// <summary>The arguments are unusable as given; fix the call.</summary>
    public const string Invalid = "Invalid";
    /// <summary>Nothing matched; <c>reason</c> is a <see cref="FixNotFoundReason"/> name.</summary>
    public const string NotFound = "NotFound";
    /// <summary>Several distinct fixes apply; <c>candidates</c> lists them.</summary>
    public const string Ambiguous = "Ambiguous";
    /// <summary>The file changed or was deleted after the fix was computed; nothing was written.</summary>
    public const string Stale = "Stale";
    /// <summary>A dependency is down; <c>reason</c> is an <see cref="UnavailableReason"/> value.</summary>
    public const string Unavailable = "Unavailable";
    /// <summary>An unexpected exception, or a refused or failed write (<c>reason</c> = the write kind).</summary>
    public const string Faulted = "Faulted";
}

/// <summary>The <c>reason</c> values of an <see cref="ToolErrorCode.Unavailable"/> error.</summary>
public static class UnavailableReason
{
    /// <summary>The server runs with <c>--no-proxy</c>.</summary>
    public const string NoProxy = "NoProxy";
    /// <summary>almcp is not in the DevTools directory.</summary>
    public const string AlmcpNotFound = "AlmcpNotFound";
    /// <summary>almcp failed to start, or has been stopped.</summary>
    public const string AlmcpNotReady = "AlmcpNotReady";
    /// <summary>The proxied call failed or almcp reported an error; <c>detail</c> carries the almcp text.</summary>
    public const string AlmcpCallFailed = "AlmcpCallFailed";
}
