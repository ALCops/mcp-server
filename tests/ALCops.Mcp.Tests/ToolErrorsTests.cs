using System.Text.Json;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>The error envelope: isError, key order and omitted optional fields.</summary>
public sealed class ToolErrorsTests
{
    private static string[] Keys(CallToolResult result) =>
        [.. JsonDocument.Parse(ToolResultAssert.Text(result)).RootElement.EnumerateObject().Select(p => p.Name)];

    [Fact]
    public void Faulted_FromException_HasErrorMessageAndDetailOnly()
    {
        var result = ToolErrors.Faulted(new InvalidOperationException("boom"));

        Assert.Equal(["error", "message", "detail"], Keys(result));
        var root = ToolResultAssert.Error(result, "Faulted");
        Assert.Equal("boom", root.GetProperty("message").GetString());
        Assert.Equal("System.InvalidOperationException", root.GetProperty("detail").GetString());
    }

    [Fact]
    public void NotFound_WithCandidates_KeepsKeyOrder()
    {
        var result = ToolErrors.NotFound(
            FixNotFoundReason.NoFixForEquivalenceKey, "no such key", "C:\\p\\a.al", "LC0020",
            [new CodeFixInfo("k1", "Title 1", "Provider")]);

        Assert.Equal(["error", "message", "reason", "candidates", "filePath", "diagnosticId"], Keys(result));
        var root = ToolResultAssert.Error(result, "NotFound", "NoFixForEquivalenceKey");
        var candidate = Assert.Single(root.GetProperty("candidates").EnumerateArray().ToList());
        Assert.Equal(["equivalenceKey", "title", "providerName"], candidate.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Invalid_OmitsEveryOptionalField()
    {
        Assert.Equal(["error", "message"], Keys(ToolErrors.Invalid("bad")));
    }

    [Fact]
    public void Ok_LeavesIsErrorUnset()
    {
        var result = ToolResults.Ok(new { applied = true });

        Assert.Null(result.IsError);
        Assert.Equal("{\"applied\":true}", ToolResultAssert.Text(result));
    }

    [Fact]
    public void EveryError_SetsIsError()
    {
        CallToolResult[] errors =
        [
            ToolErrors.Invalid("m"),
            ToolErrors.NotFound(FixNotFoundReason.NoFixProvider, "m"),
            ToolErrors.Ambiguous("m", [new CodeFixInfo("k", "t", "p")]),
            ToolErrors.Stale("f.al", "LC0020", "m"),
            ToolErrors.Unavailable(UnavailableReason.NoProxy, "m"),
            ToolErrors.Faulted(new IOException("m")),
            ToolErrors.Faulted(FileWriteConflictKind.WriteFailed, "m"),
        ];

        Assert.All(errors, e => Assert.True(e.IsError));
    }

    [Fact]
    public void NotFoundMessage_NoDiagnosticAtPosition_PointsToAnalyze()
    {
        var message = ToolErrors.NotFoundMessage(FixNotFoundReason.NoDiagnosticAtPosition, "LC0020", "C:\\p\\a.al", 3, 5);

        Assert.Equal("No LC0020 at C:\\p\\a.al:3:5; re-run analyze and use its line/column.", message);
    }
}
