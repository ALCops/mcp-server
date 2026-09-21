using System.Text.Json;
using System.Text.Json.Nodes;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ALCops.Mcp.Tests;

public sealed class CompileDiagnosticsParserTests
{
    // --- ParseLocation ---

    [Fact]
    public void ParseLocation_WindowsPath()
    {
        var loc = CompileDiagnosticsParser.ParseLocation(@"SourceFile(C:\repo\App\src\File.al@42:15)");
        Assert.Equal(@"C:\repo\App\src\File.al", loc.Path);
        Assert.Equal(42, loc.Line);
        Assert.Equal(15, loc.Column);
    }

    [Fact]
    public void ParseLocation_LinuxPath()
    {
        var loc = CompileDiagnosticsParser.ParseLocation("SourceFile(/home/u/ws/App/src/File.al@3:1)");
        Assert.Equal("/home/u/ws/App/src/File.al", loc.Path);
        Assert.Equal(3, loc.Line);
        Assert.Equal(1, loc.Column);
    }

    [Fact]
    public void ParseLocation_PathWithParenthesesAndAt()
    {
        var loc = CompileDiagnosticsParser.ParseLocation(@"SourceFile(C:\ws\My (Test) App\a@b\File.al@7:9)");
        Assert.Equal(@"C:\ws\My (Test) App\a@b\File.al", loc.Path);
        Assert.Equal(7, loc.Line);
        Assert.Equal(9, loc.Column);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("None()")]
    public void ParseLocation_NullOrNoneOrEmpty_ReturnsDefault(string? location)
    {
        var loc = CompileDiagnosticsParser.ParseLocation(location);
        Assert.Null(loc.Path);
        Assert.Null(loc.Line);
        Assert.Null(loc.Column);
    }

    [Fact]
    public void ParseLocation_BadInt_ReturnsDefault()
    {
        var loc = CompileDiagnosticsParser.ParseLocation(@"SourceFile(C:\x.al@abc:1)");
        Assert.Null(loc.Path);
    }

    [Fact]
    public void ParseLocation_XMLFile_Parsed()
    {
        var loc = CompileDiagnosticsParser.ParseLocation(@"XMLFile(C:\ws\App\Translations\App.g.xlf@1:1)");
        Assert.Equal(@"C:\ws\App\Translations\App.g.xlf", loc.Path);
        Assert.Equal(1, loc.Line);
        Assert.Equal(1, loc.Column);
    }

    // --- Parse(string) ---

    [Fact]
    public void Parse_CamelCase_Payload()
    {
        var json = """
        {
            "succeeded": false,
            "diagnostics": [
                { "severity": "Error", "code": "AL0001", "location": "SourceFile(C:\\x.al@1:1)", "description": "Syntax error" },
                { "severity": "Warning", "code": "LC0020", "location": "SourceFile(C:\\x.al@5:3)", "description": "Redundant property" }
            ],
            "message": "[Warning] Some warning"
        }
        """;

        var (diags, message, succeeded) = CompileDiagnosticsParser.Parse(json);
        Assert.False(succeeded);
        Assert.Equal(2, diags.Count);
        Assert.Equal("AL0001", diags[0].Code);
        Assert.Equal("Error", diags[0].Severity);
        Assert.Equal("LC0020", diags[1].Code);
        Assert.Equal("Warning", diags[1].Severity);
        Assert.Equal("[Warning] Some warning", message);
    }

    [Fact]
    public void Parse_PascalCase_Payload()
    {
        var json = """
        {
            "Succeeded": false,
            "Diagnostics": [
                { "Severity": "Error", "Code": "AL0001", "Location": "SourceFile(C:\\x.al@1:1)", "Description": "Syntax error" },
                { "Severity": "Warning", "Code": "LC0020", "Location": "SourceFile(C:\\x.al@5:3)", "Description": "Redundant property" }
            ],
            "Message": "[Warning] Some warning"
        }
        """;

        var (diags, _, _) = CompileDiagnosticsParser.Parse(json);
        Assert.Equal(2, diags.Count);
        Assert.Equal("AL0001", diags[0].Code);
        Assert.Equal("Error", diags[0].Severity);
    }

    [Fact]
    public void Parse_NumericSeverities()
    {
        var json = """
        {
            "diagnostics": [
                { "severity": 3, "code": "AL0001", "description": "err" },
                { "severity": 2, "code": "LC0020", "description": "warn" }
            ]
        }
        """;

        var (diags, _, succeeded) = CompileDiagnosticsParser.Parse(json);
        Assert.True(succeeded);
        Assert.Equal("Error", diags[0].Severity);
        Assert.Equal("Warning", diags[1].Severity);
    }

    [Fact]
    public void Parse_EmptyDiagnostics()
    {
        var json = """{"succeeded":true,"diagnostics":[]}""";
        var (diags, message, succeeded) = CompileDiagnosticsParser.Parse(json);
        Assert.True(succeeded);
        Assert.Empty(diags);
        Assert.Null(message);
    }

    [Fact]
    public void Parse_SkipsMissingCode_IgnoresUnknownProperties()
    {
        var json = """
        {
            "diagnostics": [
                { "severity": "Error", "code": null, "description": "skip me" },
                { "severity": "Error", "code": "", "description": "skip too" },
                { "severity": "Warning", "code": "LC0001", "description": "keep", "unknownProp": 42 }
            ],
            "unknownTop": true
        }
        """;

        var (diags, _, _) = CompileDiagnosticsParser.Parse(json);
        Assert.Single(diags);
        Assert.Equal("LC0001", diags[0].Code);
    }

    // --- Parse(CallToolResult) ---

    [Fact]
    public void Parse_CallToolResult_StructuredContentWins()
    {
        var structuredJson = """{"succeeded":true,"diagnostics":[{"severity":"Warning","code":"LC0020","description":"From structured"}]}""";
        var textJson = """{"succeeded":true,"diagnostics":[{"severity":"Error","code":"AL0001","description":"From text"}]}""";

        var result = new CallToolResult
        {
            StructuredContent = JsonNode.Parse(structuredJson),
            Content = [new TextContentBlock { Text = textJson }],
        };

        var (diags, _, _) = CompileDiagnosticsParser.Parse(result);
        Assert.Single(diags);
        Assert.Equal("LC0020", diags[0].Code);
    }

    [Fact]
    public void Parse_CallToolResult_NonJsonTextThenJsonText()
    {
        var result = new CallToolResult
        {
            Content = [
                new TextContentBlock { Text = "Compiled." },
                new TextContentBlock { Text = """{"succeeded":true,"diagnostics":[{"severity":"Warning","code":"LC0020","description":"ok"}]}""" }
            ],
        };

        var (diags, _, _) = CompileDiagnosticsParser.Parse(result);
        Assert.Single(diags);
        Assert.Equal("LC0020", diags[0].Code);
    }

    [Fact]
    public void Parse_CallToolResult_NoJson_Throws()
    {
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "Compiled successfully." }],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => CompileDiagnosticsParser.Parse(result));
        Assert.Contains("almcp returned no JSON payload", ex.Message);
    }

    // --- ExtractWarnings ---

    [Fact]
    public void ExtractWarnings_MultipleLines()
    {
        var warnings = CompileDiagnosticsParser.ExtractWarnings("[Warning] A\n[Warning] B\n");
        Assert.Equal(2, warnings.Count);
        Assert.Equal("A", warnings[0]);
        Assert.Equal("B", warnings[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractWarnings_NullOrWhitespace_Empty(string? input)
    {
        Assert.Empty(CompileDiagnosticsParser.ExtractWarnings(input));
    }

    [Fact]
    public void ExtractWarnings_UnprefixedMessage_KeptVerbatim()
    {
        var warnings = CompileDiagnosticsParser.ExtractWarnings("Something unexpected happened");
        Assert.Single(warnings);
        Assert.Equal("Something unexpected happened", warnings[0]);
    }

    // --- IsCompilerDiagnostic ---

    [Theory]
    [InlineData("AL0432", true)]
    [InlineData("AL0001", true)]
    [InlineData("AL432", false)]
    [InlineData("ALC0001", false)]
    [InlineData("LC0020", false)]
    [InlineData("al0432", false)]
    public void IsCompilerDiagnostic_Cases(string code, bool expected)
    {
        Assert.Equal(expected, CompileDiagnosticsParser.IsCompilerDiagnostic(code));
    }

    // --- Enrich ---

    [Fact]
    public void Enrich_CompilerDiagnostic_OverridesCopNameAndHasFix()
    {
        var raw = new List<RawCompileDiagnostic>
        {
            new("Error", "AL0432", @"SourceFile(C:\x.al@1:1)", "Some error"),
        };

        var enriched = CompileDiagnosticsParser.Enrich(
            raw,
            _ => "Unknown",
            _ => true,
            p => p);

        Assert.Single(enriched);
        Assert.Equal("Compiler", enriched[0].Analyzer);
        Assert.False(enriched[0].HasFix);
    }

    [Fact]
    public void Enrich_CopDiagnostic_PropagatesCopNameAndHasFix()
    {
        var raw = new List<RawCompileDiagnostic>
        {
            new("Warning", "LC0020", @"SourceFile(C:\x.al@5:3)", "Redundant property"),
        };

        var enriched = CompileDiagnosticsParser.Enrich(
            raw,
            _ => "ALCops.LinterCop",
            _ => true,
            p => p);

        Assert.Single(enriched);
        Assert.Equal("ALCops.LinterCop", enriched[0].Analyzer);
        Assert.True(enriched[0].HasFix);
    }

    [Fact]
    public void Enrich_NormalizePath_CalledOnlyWhenLocated()
    {
        var normalizeCount = 0;
        var raw = new List<RawCompileDiagnostic>
        {
            new("Warning", "LC0020", @"SourceFile(C:\x.al@5:3)", "Located"),
            new("Error", "AL0001", "None", "Unlocated"),
        };

        CompileDiagnosticsParser.Enrich(
            raw,
            _ => "Unknown",
            _ => false,
            p => { normalizeCount++; return p; });

        Assert.Equal(1, normalizeCount);
    }

    // --- Filter ---

    [Fact]
    public void Filter_FilePathCaseInsensitive()
    {
        var diags = new List<AnalyzeDiagnostic>
        {
            new(@"C:\ws\App\MyPage.al", 1, 1, "LC0020", "Warning", "msg", "Cop", false),
            new(@"C:\ws\App\Other.al", 2, 1, "LC0020", "Warning", "msg", "Cop", false),
        };

        var (filtered, _) = CompileDiagnosticsParser.Filter(diags, new AnalyzeFilter(
            @"c:\WS\app\MYPAGE.AL", null, null, false, null, null, null));

        Assert.Single(filtered);
        Assert.Equal(@"C:\ws\App\MyPage.al", filtered[0].FilePath);
    }

    [Fact]
    public void Filter_FolderPath_PrefixMatchWithSeparator()
    {
        var sep = Path.DirectorySeparatorChar;
        var inside = $"C:{sep}ws{sep}App{sep}src{sep}X.al";
        var sibling = $"C:{sep}ws{sep}AppSource{sep}X.al";
        var exact = $"C:{sep}ws{sep}App";

        var diags = new List<AnalyzeDiagnostic>
        {
            new(inside, 1, 1, "LC0020", "Warning", "msg", "Cop", false),
            new(sibling, 2, 1, "LC0020", "Warning", "msg", "Cop", false),
            new(exact, 3, 1, "LC0020", "Warning", "msg", "Cop", false),
        };

        var (filtered, _) = CompileDiagnosticsParser.Filter(diags, new AnalyzeFilter(
            null, $"C:{sep}ws{sep}App", null, false, null, null, null));

        Assert.Single(filtered);
        Assert.Equal(inside, filtered[0].FilePath);
    }

    [Fact]
    public void Filter_ProjectPathAndFilePath_AND()
    {
        var sep = Path.DirectorySeparatorChar;
        var match = $"C:{sep}ws{sep}App{sep}MyPage.al";
        var other = $"C:{sep}ws{sep}App{sep}Other.al";

        var diags = new List<AnalyzeDiagnostic>
        {
            new(match, 1, 1, "LC0020", "Warning", "msg", "Cop", false),
            new(other, 2, 1, "LC0020", "Warning", "msg", "Cop", false),
        };

        var (filtered, _) = CompileDiagnosticsParser.Filter(diags, new AnalyzeFilter(
            match, null, $"C:{sep}ws{sep}App", false, null, null, null));

        Assert.Single(filtered);
        Assert.Equal(match, filtered[0].FilePath);
    }

    [Fact]
    public void Filter_UnlocatedDiagnostics_KeptWhenIncludeUnlocated()
    {
        var diags = new List<AnalyzeDiagnostic>
        {
            new(null, null, null, "AL0001", "Error", "Unlocated", "Compiler", false),
        };

        var (kept, droppedKept) = CompileDiagnosticsParser.Filter(diags, new AnalyzeFilter(null, null, null, true, null, null, null));
        Assert.Single(kept);
        Assert.Equal(0, droppedKept);
    }

    [Fact]
    public void Filter_UnlocatedDiagnostics_DroppedWhenNotIncludeUnlocated()
    {
        var diags = new List<AnalyzeDiagnostic>
        {
            new(null, null, null, "AL0001", "Error", "Unlocated", "Compiler", false),
        };

        var (dropped, droppedCount) = CompileDiagnosticsParser.Filter(diags, new AnalyzeFilter(null, null, null, false, null, null, null));
        Assert.Empty(dropped);
        Assert.Equal(1, droppedCount);
    }

    [Fact]
    public void Filter_UnlocatedDiagnostics_DroppedWithScope()
    {
        var diags = new List<AnalyzeDiagnostic>
        {
            new(null, null, null, "AL0001", "Error", "Unlocated", "Compiler", false),
        };

        var (filtered, droppedCount) = CompileDiagnosticsParser.Filter(diags, new AnalyzeFilter(
            null, null, "C:\\ws\\App", false, null, null, null));
        Assert.Empty(filtered);
        Assert.Equal(1, droppedCount);
    }

    [Fact]
    public void Filter_Severities_CaseInsensitive()
    {
        var diags = new List<AnalyzeDiagnostic>
        {
            new("a.al", 1, 1, "LC0020", "Warning", "msg", "Cop", false),
            new("b.al", 1, 1, "AL0001", "Error", "msg", "Compiler", false),
        };

        var (filtered, _) = CompileDiagnosticsParser.Filter(diags, new AnalyzeFilter(
            null, null, null, false,
            new HashSet<string>(["warning"], StringComparer.OrdinalIgnoreCase),
            null, null));

        Assert.Single(filtered);
        Assert.Equal("Warning", filtered[0].Severity);
    }

    [Fact]
    public void Filter_Analyzers_CaseInsensitive()
    {
        var diags = new List<AnalyzeDiagnostic>
        {
            new("a.al", 1, 1, "AL0432", "Error", "msg", "Compiler", false),
            new("b.al", 1, 1, "LC0020", "Warning", "msg", "ALCops.LinterCop", true),
            new("c.al", 1, 1, "AA0001", "Warning", "msg", "CodeCop", false),
        };

        var (filtered, _) = CompileDiagnosticsParser.Filter(diags, new AnalyzeFilter(
            null, null, null, false, null,
            new HashSet<string>(["compiler", "alcops.lintercop"], StringComparer.OrdinalIgnoreCase),
            null));

        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void Filter_RuleIds_CaseInsensitive()
    {
        var diags = new List<AnalyzeDiagnostic>
        {
            new("a.al", 1, 1, "LC0020", "Warning", "msg", "Cop", false),
            new("b.al", 1, 1, "AL0001", "Error", "msg", "Compiler", false),
        };

        var (filtered, _) = CompileDiagnosticsParser.Filter(diags, new AnalyzeFilter(
            null, null, null, false, null, null,
            new HashSet<string>(["lc0020"], StringComparer.OrdinalIgnoreCase)));

        Assert.Single(filtered);
        Assert.Equal("LC0020", filtered[0].Id);
    }

    // --- Sort/Build ---

    [Fact]
    public void Sort_OrdersByFilePathLinColumnId()
    {
        var diags = new List<AnalyzeDiagnostic>
        {
            new("c.al", 2, 1, "LC0020", "Warning", "msg", "Cop", false),
            new(null, null, null, "AL0001", "Error", "msg", "Compiler", false),
            new("a.al", 1, 1, "LC0020", "Warning", "msg", "Cop", false),
            new("B.al", 1, 1, "LC0020", "Warning", "msg", "Cop", false),
            new("a.al", 1, 1, "AL0001", "Error", "msg", "Compiler", false),
        };

        var sorted = CompileDiagnosticsParser.Sort(diags);

        Assert.Null(sorted[0].FilePath);
        Assert.Equal("a.al", sorted[1].FilePath);
        Assert.Equal("AL0001", sorted[1].Id);
        Assert.Equal("a.al", sorted[2].FilePath);
        Assert.Equal("LC0020", sorted[2].Id);
        Assert.Equal("B.al", sorted[3].FilePath);
        Assert.Equal("c.al", sorted[4].FilePath);
    }

    [Fact]
    public void Build_TruncatesAndSummarizes()
    {
        var diags = Enumerable.Range(1, 7).Select(i =>
            new AnalyzeDiagnostic($"file{i}.al", i, 1,
                i <= 3 ? "LC0020" : "AL0001",
                i <= 3 ? "Warning" : "Error",
                $"msg{i}",
                i <= 3 ? "ALCops.LinterCop" : "Compiler",
                i <= 3)).ToList();

        var result = CompileDiagnosticsParser.Build("C:\\ws\\App", diags, 5, []);

        Assert.Equal("C:\\ws\\App", result.Project);
        Assert.Equal(5, result.Count);
        Assert.Equal(7, result.TotalCount);
        Assert.True(result.Truncated);
        Assert.Equal(3, result.Summary.BySeverity["Warning"]);
        Assert.Equal(4, result.Summary.BySeverity["Error"]);
        Assert.Equal(3, result.Summary.ByAnalyzer["ALCops.LinterCop"]);
        Assert.Equal(4, result.Summary.ByAnalyzer["Compiler"]);
        Assert.Null(result.Warnings);
    }

    [Fact]
    public void Build_NoTruncation()
    {
        var diags = Enumerable.Range(1, 3).Select(i =>
            new AnalyzeDiagnostic($"file{i}.al", i, 1, "LC0020", "Warning", $"msg{i}", "Cop", false)).ToList();

        var result = CompileDiagnosticsParser.Build("C:\\ws\\App", diags, 500, ["A warning"]);

        Assert.Equal(3, result.Count);
        Assert.Equal(3, result.TotalCount);
        Assert.False(result.Truncated);
        Assert.NotNull(result.Warnings);
        Assert.Single(result.Warnings);
    }

    // --- Serialized key order ---

    [Fact]
    public void AnalyzeResult_SerializedKeyOrder()
    {
        var result = new AnalyzeResult(
            "C:\\ws\\App", 1, 1, false,
            new AnalyzeSummary(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Warning"] = 1 },
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Cop"] = 1 }),
            [new AnalyzeDiagnostic("a.al", 1, 1, "LC0020", "Warning", "msg", "Cop", false)],
            null);

        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        using var doc = JsonDocument.Parse(json);

        var topKeys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["project", "count", "totalCount", "truncated", "summary", "diagnostics", "warnings"], topKeys);

        var diagKeys = doc.RootElement.GetProperty("diagnostics")[0].EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["filePath", "line", "column", "id", "severity", "message", "analyzer", "hasFix"], diagKeys);
    }

    // --- FindContainingProject ---

    [Fact]
    public void FindContainingProject_FileInsideProject_ReturnsProject()
    {
        var sep = Path.DirectorySeparatorChar;
        var projects = new List<string> { $"C:{sep}ws{sep}App" };
        var file = $"C:{sep}ws{sep}App{sep}src{sep}X.al";

        Assert.Equal($"C:{sep}ws{sep}App", CompileDiagnosticsParser.FindContainingProject(file, projects));
    }

    [Fact]
    public void FindContainingProject_ExactMatch_ReturnsProject()
    {
        var sep = Path.DirectorySeparatorChar;
        var projects = new List<string> { $"C:{sep}ws{sep}App" };
        var path = $"C:{sep}ws{sep}App";

        Assert.Equal($"C:{sep}ws{sep}App", CompileDiagnosticsParser.FindContainingProject(path, projects));
    }

    [Fact]
    public void FindContainingProject_SiblingFolder_ReturnsNull()
    {
        var sep = Path.DirectorySeparatorChar;
        var projects = new List<string> { $"C:{sep}ws{sep}App" };
        var file = $"C:{sep}ws{sep}AppSource{sep}X.al";

        Assert.Null(CompileDiagnosticsParser.FindContainingProject(file, projects));
    }

    [Fact]
    public void FindContainingProject_LongestPrefixWins()
    {
        var sep = Path.DirectorySeparatorChar;
        var projects = new List<string>
        {
            $"C:{sep}ws",
            $"C:{sep}ws{sep}App"
        };
        var file = $"C:{sep}ws{sep}App{sep}src{sep}X.al";

        Assert.Equal($"C:{sep}ws{sep}App", CompileDiagnosticsParser.FindContainingProject(file, projects));
    }

    [Fact]
    public void FindContainingProject_NoMatch_ReturnsNull()
    {
        var sep = Path.DirectorySeparatorChar;
        var projects = new List<string> { $"C:{sep}ws{sep}App" };
        var file = $"D:{sep}other{sep}X.al";

        Assert.Null(CompileDiagnosticsParser.FindContainingProject(file, projects));
    }

    // --- Succeeded=false warning text ---

    [Fact]
    public void SucceededFalse_WarningText_Format()
    {
        var rawCount = 5;
        var filteredCount = 3;
        var expected = $"al_compile reported succeeded=false: {rawCount} diagnostics workspace-wide, {filteredCount} after filtering.";

        Assert.Contains("succeeded=false", expected);
        Assert.Contains("5 diagnostics workspace-wide", expected);
        Assert.Contains("3 after filtering", expected);
    }

    [Fact]
    public void DroppedUnlocated_WarningText_Format()
    {
        var n = 2;
        var expected = $"{n} diagnostic(s) without a file location were excluded by the scope filter; call analyze without scope arguments to see them.";

        Assert.Contains("2 diagnostic(s)", expected);
        Assert.Contains("scope filter", expected);
    }
}
