using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ALCops.Mcp.Tools;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// Regression test for the mcp-server README claim (previously stale, see issue #15) that
/// apply_fix writes the modified content to disk. This locks in the actual behavior of
/// ApplyFixTool so documentation cannot silently drift from the implementation again.
/// </summary>
public class ApplyFixToolTests
{
    [Fact]
    public async Task ApplyFix_WritesModifiedContentToDisk()
    {
        // Copy the fixture to a temp directory so this test can safely mutate the file
        // without affecting the checked-in fixture used by other tests/runs.
        var tempProjectPath = TestAnalyzers.CopyFixtureWithAnalyzers("ApplyFixProject", "alcops-applyfix-test");

        try
        {
            var filePath = Path.Combine(tempProjectPath, "MyPage.al");
            var originalContent = await File.ReadAllTextAsync(filePath);

            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var codeFixRunner = new CodeFixRunner();
            var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();

            var session = await sessionManager.GetOrLoadProjectAsync(tempProjectPath);
            var analyzerSet = await analyzerResolver.ResolveAsync(tempProjectPath, null);

            // LC0020 (ApplicationAreaRedundancy) on the field-level ApplicationArea in MyPage.al.
            // Hardcoded rather than discovered, matching GetFixes_RulesetSuppressesRule_ReturnsSuppressedByRuleset.
            const int line = 11, column = 17;
            var lookup = await codeFixRunner.GetFixesAsync(
                session, filePath, "LC0020", line, column, analyzerSet);

            Assert.True(lookup.NotFoundReason is null && lookup.Fixes.Count > 0,
                $"Expected a fixable LC0020 at line {line}, column {column}, got {lookup.NotFoundReason}. Loaded {analyzerSet.GetAllAnalyzers().Length} " +
                $"analyzer(s); warnings: {string.Join("; ", analyzerSet.Warnings)}. " +
                "The fixture, the location, or the analyzer configuration may have changed.");

            var result = await ApplyFixTool.ApplyFix(
                sessionManager, codeFixRunner, analyzerResolver, new GuardedFileWriter(),
                tempProjectPath, filePath, "LC0020", line, column,
                lookup.Fixes[0].EquivalenceKey);

            Assert.True(ToolResultAssert.Ok(result).GetProperty("applied").GetBoolean());

            // The actual regression assertion: the file on disk must have changed.
            var updatedContent = await File.ReadAllTextAsync(filePath);
            Assert.NotEqual(originalContent, updatedContent);
        }
        finally
        {
            if (Directory.Exists(tempProjectPath))
                Directory.Delete(tempProjectPath, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyFix_ExternalEditBetweenCalls_PreservesEditAndAppliesFix()
    {
        var tempProjectPath = TestAnalyzers.CopyFixtureWithAnalyzers("ApplyFixProject", "alcops-stale-applyfix-test");

        try
        {
            var filePath = Path.Combine(tempProjectPath, "MyPage.al");

            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var codeFixRunner = new CodeFixRunner();
            var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();

            // Prime — loads the session and discovers the diagnostic.
            var session = await sessionManager.GetOrLoadProjectAsync(tempProjectPath);
            var analyzerSet = await analyzerResolver.ResolveAsync(tempProjectPath, null);

            const int line = 11, column = 17;
            var lookup = await codeFixRunner.GetFixesAsync(
                session, filePath, "LC0020", line, column, analyzerSet);
            Assert.True(lookup.Fixes.Count > 0, "Expected a fixable LC0020.");

            // External edit: append a comment as the final line. Line 11 stays valid.
            var content = await File.ReadAllTextAsync(filePath);
            await File.WriteAllTextAsync(filePath, content + "\n// edited");

            // Apply — the refresh must pick up the appended line, and the guarded write must
            // succeed because the fix was computed from the refreshed content.
            var result = await ApplyFixTool.ApplyFix(
                sessionManager, codeFixRunner, analyzerResolver, new GuardedFileWriter(),
                tempProjectPath, filePath, "LC0020", line, column,
                lookup.Fixes[0].EquivalenceKey);

            Assert.True(ToolResultAssert.Ok(result).GetProperty("applied").GetBoolean());

            var finalContent = await File.ReadAllTextAsync(filePath);
            Assert.Contains("// edited", finalContent);
            Assert.Equal(1, CountOccurrences(finalContent, "ApplicationArea = All;"));
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(tempProjectPath);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    [Fact]
    public async Task ApplyFix_FileWithUtf8Bom_KeepsBom()
    {
        var tempProjectPath = TestAnalyzers.CopyFixtureWithAnalyzers("ApplyFixProject", "alcops-applyfix-bom-test");

        try
        {
            // The BOM is added at test time; a committed fixture with a BOM could be mangled by git.
            var filePath = Path.Combine(tempProjectPath, "MyPage.al");
            var bareBytes = await File.ReadAllBytesAsync(filePath);
            await File.WriteAllBytesAsync(filePath, [0xEF, 0xBB, 0xBF, .. bareBytes]);

            var (result, _) = await ApplyLc0020Async(tempProjectPath, filePath, new GuardedFileWriter());

            Assert.True(ToolResultAssert.Ok(result).GetProperty("applied").GetBoolean());

            var written = await File.ReadAllBytesAsync(filePath);
            Assert.Equal([0xEF, 0xBB, 0xBF], written.Take(3));
            Assert.NotEqual(0xEF, written[3]); // exactly one BOM
            Assert.NotEqual(bareBytes, written.Skip(3).ToArray());
            Assert.Empty(Directory.GetFiles(tempProjectPath, "*" + GuardedFileWriter.TempSuffix, SearchOption.AllDirectories));
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(tempProjectPath);
        }
    }

    [Fact]
    public async Task ApplyFix_WriteFails_ReturnsFaultedWriteFailedAndLeavesFileIntact()
    {
        var tempProjectPath = TestAnalyzers.CopyFixtureWithAnalyzers("ApplyFixProject", "alcops-applyfix-ioerr-test");

        try
        {
            var filePath = Path.Combine(tempProjectPath, "MyPage.al");
            var originalBytes = await File.ReadAllBytesAsync(filePath);

            var writer = new GuardedFileWriter((_, _) => throw new IOException("injected move failure"));
            var (result, _) = await ApplyLc0020Async(tempProjectPath, filePath, writer);

            var root = ToolResultAssert.Error(result, "Faulted", "WriteFailed");
            Assert.Equal("System.IO.IOException", root.GetProperty("detail").GetString());
            Assert.Contains("injected move failure", root.GetProperty("message").GetString());
            Assert.Equal(filePath, root.GetProperty("filePath").GetString());
            Assert.Equal("LC0020", root.GetProperty("diagnosticId").GetString());
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(filePath));
            Assert.Empty(Directory.GetFiles(tempProjectPath, "*" + GuardedFileWriter.TempSuffix, SearchOption.AllDirectories));
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(tempProjectPath);
        }
    }

    [Fact]
    public async Task ApplyFix_FileNotValidUtf8_ReturnsFaultedUnsupportedEncodingAndLeavesFileIntact()
    {
        var tempProjectPath = TestAnalyzers.CopyFixtureWithAnalyzers("ApplyFixProject", "alcops-applyfix-cp1252-test");

        try
        {
            // The fixture has no comment or string literal to alter, so append a comment holding a
            // Windows-1252 "é" (0xE9). The loader decodes it with replacement, so LC0020 is still found.
            var filePath = Path.Combine(tempProjectPath, "MyPage.al");
            var bareBytes = await File.ReadAllBytesAsync(filePath);
            byte[] originalBytes = [.. bareBytes, .. "\n// caf"u8, 0xE9, (byte)'\n'];
            await File.WriteAllBytesAsync(filePath, originalBytes);

            var (result, _) = await ApplyLc0020Async(tempProjectPath, filePath, new GuardedFileWriter());

            var root = ToolResultAssert.Error(result, "Faulted", "UnsupportedEncoding");
            Assert.Contains("not valid in its detected encoding (UTF-8;", root.GetProperty("message").GetString());
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(filePath));
            Assert.Empty(Directory.GetFiles(tempProjectPath, "*" + GuardedFileWriter.TempSuffix, SearchOption.AllDirectories));
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(tempProjectPath);
        }
    }

    [Fact]
    public async Task ApplyFix_FileChangedButRefreshSkipsIt_ReturnsStaleAndWritesNothing()
    {
        var tempProjectPath = TestAnalyzers.CopyFixtureWithAnalyzers("ApplyFixProject", "alcops-applyfix-stale-test");

        try
        {
            var filePath = Path.Combine(tempProjectPath, "MyPage.al");

            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var codeFixRunner = new CodeFixRunner();
            var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();

            // Prime the session so the cached text is the original.
            var session = await sessionManager.GetOrLoadProjectAsync(tempProjectPath);
            var analyzerSet = await analyzerResolver.ResolveAsync(tempProjectPath, null);
            var lookup = await codeFixRunner.GetFixesAsync(session, filePath, "LC0020", 11, 17, analyzerSet);
            Assert.True(lookup.Fixes.Count > 0, "Expected a fixable LC0020 at line 11, column 17.");

            // A same-length edit with the old timestamp: the refresh's length+mtime gate skips the
            // file, so the fix is computed from the cached text and the guarded write must refuse it.
            var stamp = File.GetLastWriteTimeUtc(filePath);
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("50100", content);
            await File.WriteAllTextAsync(filePath, content.Replace("50100", "50109"));
            File.SetLastWriteTimeUtc(filePath, stamp);
            var editedBytes = await File.ReadAllBytesAsync(filePath);

            var result = await ApplyFixTool.ApplyFix(
                sessionManager, codeFixRunner, analyzerResolver, new GuardedFileWriter(),
                tempProjectPath, filePath, "LC0020", 11, 17, lookup.Fixes[0].EquivalenceKey);

            var root = ToolResultAssert.Error(result, "Stale");
            Assert.Equal(filePath, root.GetProperty("filePath").GetString());
            Assert.Equal("LC0020", root.GetProperty("diagnosticId").GetString());
            Assert.False(root.TryGetProperty("reason", out _));
            Assert.Equal(editedBytes, await File.ReadAllBytesAsync(filePath));
            Assert.Empty(Directory.GetFiles(tempProjectPath, "*" + GuardedFileWriter.TempSuffix, SearchOption.AllDirectories));
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(tempProjectPath);
        }
    }

    [Fact]
    public async Task ApplyFix_UnknownEquivalenceKey_ReturnsNotFoundWithCandidates()
    {
        var tempProjectPath = TestAnalyzers.CopyFixtureWithAnalyzers("ApplyFixProject", "alcops-applyfix-badkey-test");

        try
        {
            var filePath = Path.Combine(tempProjectPath, "MyPage.al");
            var originalBytes = await File.ReadAllBytesAsync(filePath);

            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();

            var result = await ApplyFixTool.ApplyFix(
                sessionManager, new CodeFixRunner(), analyzerResolver, new GuardedFileWriter(),
                tempProjectPath, filePath, "LC0020", 11, 17, "no-such-key");

            var root = ToolResultAssert.Error(result, "NotFound", "NoFixForEquivalenceKey");
            var candidates = root.GetProperty("candidates").EnumerateArray().ToList();
            Assert.NotEmpty(candidates);
            Assert.All(candidates, c =>
            {
                Assert.True(c.TryGetProperty("equivalenceKey", out _));
                Assert.False(string.IsNullOrEmpty(c.GetProperty("title").GetString()));
                Assert.False(string.IsNullOrEmpty(c.GetProperty("providerName").GetString()));
            });
            Assert.Contains("no-such-key", root.GetProperty("message").GetString());
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(filePath));
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(tempProjectPath);
        }
    }

    [Fact]
    public async Task ApplyFix_RuleWithoutFixProvider_ReturnsNotFoundNoFixProvider()
    {
        var tempProjectPath = TestAnalyzers.CopyFixtureWithAnalyzers("ApplyFixProject", "alcops-applyfix-noprovider-test");

        try
        {
            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();

            var result = await ApplyFixTool.ApplyFix(
                sessionManager, new CodeFixRunner(), analyzerResolver, new GuardedFileWriter(),
                tempProjectPath, Path.Combine(tempProjectPath, "MyPage.al"), "ZZ9999", 11, 17, "any");

            var root = ToolResultAssert.Error(result, "NotFound", "NoFixProvider");
            Assert.False(root.TryGetProperty("candidates", out _));
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(tempProjectPath);
        }
    }

    [Fact]
    public async Task ApplyFix_FolderWithoutAppJson_ReturnsInvalid()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"alcops-applyfix-noappjson-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        try
        {
            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();

            var result = await ApplyFixTool.ApplyFix(
                sessionManager, new CodeFixRunner(), analyzerResolver, new GuardedFileWriter(),
                folder, Path.Combine(folder, "MyPage.al"), "LC0020", 11, 17, "any");

            var root = ToolResultAssert.Error(result, "Invalid");
            Assert.Contains("app.json", root.GetProperty("message").GetString());
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(folder);
        }
    }

    [Theory]
    [InlineData("C:\\bad\0file.al")]
    [InlineData("")]
    public async Task ApplyFix_MalformedFilePath_ReturnsInvalid(string filePath)
    {
        var tempProjectPath = TestAnalyzers.CopyFixtureWithAnalyzers("ApplyFixProject", "alcops-applyfix-badfile-test");

        try
        {
            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();

            var result = await ApplyFixTool.ApplyFix(
                sessionManager, new CodeFixRunner(), analyzerResolver, new GuardedFileWriter(),
                tempProjectPath, filePath, "LC0020", 11, 17, "any");

            var root = ToolResultAssert.Error(result, "Invalid");
            Assert.Contains("filePath", root.GetProperty("message").GetString());
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(tempProjectPath);
        }
    }

    /// <summary>Loads the project, finds the LC0020 fix at MyPage.al:11:17 and applies it through the tool.</summary>
    private static async Task<(ModelContextProtocol.Protocol.CallToolResult Result, string EquivalenceKey)> ApplyLc0020Async(
        string projectPath, string filePath, GuardedFileWriter writer)
    {
        using var sessionManager = new ProjectSessionManager(new ProjectLoader());
        var codeFixRunner = new CodeFixRunner();
        var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();

        var session = await sessionManager.GetOrLoadProjectAsync(projectPath);
        var analyzerSet = await analyzerResolver.ResolveAsync(projectPath, null);

        const int line = 11, column = 17;
        var lookup = await codeFixRunner.GetFixesAsync(session, filePath, "LC0020", line, column, analyzerSet);
        Assert.True(lookup.Fixes.Count > 0, "Expected a fixable LC0020 at line 11, column 17.");

        var result = await ApplyFixTool.ApplyFix(
            sessionManager, codeFixRunner, analyzerResolver, writer,
            projectPath, filePath, "LC0020", line, column,
            lookup.Fixes[0].EquivalenceKey);

        return (result, lookup.Fixes[0].EquivalenceKey);
    }

    [Fact]
    public async Task GetFixes_RulesetSuppressesRule_ReturnsSuppressedByRuleset()
    {
        // FixAllRulesetProject ships a custom.ruleset.json setting LC0020 to "None". Even though
        // PageA.al still has a redundant ApplicationArea, get_fixes must not offer a fix for it,
        // and must say the ruleset is why.
        var tempProjectPath = TestAnalyzers.CopyFixtureWithAnalyzers("FixAllRulesetProject", "alcops-ruleset-getfixes-test");

        try
        {
            var filePath = Path.Combine(tempProjectPath, "PageA.al");

            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var codeFixRunner = new CodeFixRunner();
            var (analyzerResolver, loader) = TestAnalyzers.CreateAnalyzerResolver();

            var session = await sessionManager.GetOrLoadProjectAsync(tempProjectPath);

            // Control: with the same analyzers but no ruleset applied, the location must resolve to a
            // real fixable diagnostic — proving the location itself is correct and that the ruleset
            // (not a location mismatch) is what suppresses the result below.
            const int line = 11, column = 17;
            var withoutRuleset = TestAnalyzers.LoadAnalyzersWithoutRuleset(loader, tempProjectPath);
            var control = await codeFixRunner.GetFixesAsync(
                session, filePath, "LC0020", line, column, withoutRuleset);
            Assert.True(control.NotFoundReason is null && control.Fixes.Count > 0,
                "Expected a fixable LC0020 at line 11, column 17 with no ruleset applied — fixture or location may have changed.");

            // With the resolved AnalyzerSet (loads FixAllRulesetProject's custom.ruleset.json,
            // which sets LC0020 to "None"), the same location must yield no fixes.
            var analyzerSet = await analyzerResolver.ResolveAsync(tempProjectPath, null);
            var lookup = await codeFixRunner.GetFixesAsync(
                session, filePath, "LC0020", line, column, analyzerSet);

            Assert.Equal(FixNotFoundReason.SuppressedByRuleset, lookup.NotFoundReason);
            Assert.Empty(lookup.Fixes);
        }
        finally
        {
            if (Directory.Exists(tempProjectPath))
                Directory.Delete(tempProjectPath, recursive: true);
        }
    }
}
