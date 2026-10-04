using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALCops.Mcp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace ALCops.Mcp.Tests.Contracts;

/// <summary>
/// Builds the server's real container once (<see cref="McpHost.ConfigureServices"/> with
/// <c>--no-proxy</c>) and exposes the native tools exactly as <c>tools/list</c> publishes them.
/// No host runs, so the hosted services never start and nothing touches NuGet or almcp.
/// </summary>
public sealed class ToolContractFixture : IDisposable
{
    public ToolContractFixture()
    {
        var services = new ServiceCollection().AddLogging();
        McpHost.ConfigureServices(services, TestAnalyzers.ToolsLocator,
            ProxyOptions.Parse(["--no-proxy", "--alcops-analyzers", "off"]));
        Provider = services.BuildServiceProvider();
        Tools = Provider.GetServices<McpServerTool>().ToList();
    }

    public ServiceProvider Provider { get; }

    /// <summary>What the SDK fills <see cref="McpServerOptions.ToolCollection"/> from.</summary>
    public IReadOnlyList<McpServerTool> Tools { get; }

    public McpServerTool Tool(string name) => Tools.Single(t => t.ProtocolTool.Name == name);

    /// <summary>The tool as it goes on the wire.</summary>
    public static JsonElement Wire(McpServerTool tool) =>
        JsonSerializer.SerializeToElement(tool.ProtocolTool, McpJsonUtilities.DefaultOptions);

    /// <summary>The tool's <see cref="MethodInfo"/>, which the SDK adds to its metadata.</summary>
    public static MethodInfo Method(McpServerTool tool)
    {
        var methods = tool.Metadata.OfType<MethodInfo>().ToList();
        Assert.True(methods.Count == 1,
            $"Expected exactly one MethodInfo in the metadata of '{tool.ProtocolTool.Name}', found {methods.Count}.");
        return methods[0];
    }

    public void Dispose() => Provider.Dispose();
}

/// <summary>
/// The published <c>tools/list</c> is the only contract most MCP clients see. These tests pin it:
/// names, titles, hints, the input schema's shape and the sentences callers rely on.
/// </summary>
public sealed class ToolContractTests(ToolContractFixture fixture) : IClassFixture<ToolContractFixture>
{
    public static TheoryData<string, string, bool, bool, bool, bool> Annotations => new()
    {
        // name, title, readOnly, destructive, idempotent, openWorld
        { "analyze", "Analyze AL project", true, false, true, false },
        { "list_rules", "List analyzer rules", true, false, true, false },
        { "get_fixes", "Get code fixes", true, false, true, false },
        { "apply_fix", "Apply code fix", false, false, false, false },
        { "apply_fix_all", "Apply fix to all occurrences", false, false, false, false },
    };

    public static TheoryData<string> ToolNames => ["analyze", "list_rules", "get_fixes", "apply_fix", "apply_fix_all"];

    private static readonly string[] ExpectedNames = ["analyze", "apply_fix", "apply_fix_all", "get_fixes", "list_rules"];

    [Fact]
    public void ToolsList_IsExactlyTheFiveNativeTools_UnderNoProxy()
    {
        Assert.Equal(ExpectedNames, fixture.Tools.Select(t => t.ProtocolTool.Name).Order(StringComparer.Ordinal));

        // Under --no-proxy there is no custom list/call handler, so tools/list is the ToolCollection alone.
        var options = fixture.Provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        Assert.Null(options.Handlers.ListToolsHandler);
        Assert.Null(options.Handlers.CallToolHandler);
        Assert.NotNull(options.ToolCollection);
        Assert.Equal(ExpectedNames, options.ToolCollection.Select(t => t.ProtocolTool.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ToolsList_IsTheSameFiveNativeTools_WithProxyHandlers_UnderDefaultMode()
    {
        // Same registration as the fixture but without --no-proxy. No host runs and AlMcpProxy is never
        // resolved, so almcp is not started.
        var services = new ServiceCollection().AddLogging();
        McpHost.ConfigureServices(services, TestAnalyzers.ToolsLocator,
            ProxyOptions.Parse(["--alcops-analyzers", "off"]));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(ExpectedNames,
            provider.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).Order(StringComparer.Ordinal));

        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        Assert.NotNull(options.Handlers.ListToolsHandler);
        Assert.NotNull(options.Handlers.CallToolHandler);
        Assert.NotNull(options.ToolCollection);
        Assert.Equal(ExpectedNames, options.ToolCollection.Select(t => t.ProtocolTool.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void InputSchema_HasNoDefaultKeywordAnywhere(string name)
    {
        var schema = ToolContractFixture.Wire(fixture.Tool(name)).GetProperty("inputSchema");

        var paths = new List<string>();
        CollectDefaultKeywords(schema, "$", paths);

        Assert.Empty(paths);
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void InputSchema_PublishesExactlyTheUserParameters_AndRequiresThoseWithoutDefault(string name)
    {
        var tool = fixture.Tool(name);
        var isService = fixture.Provider.GetRequiredService<IServiceProviderIsService>();
        var userParameters = ToolContractFixture.Method(tool).GetParameters()
            .Where(p => p.ParameterType != typeof(IServiceProvider)
                && p.ParameterType != typeof(CancellationToken)
                && !isService.IsService(p.ParameterType))
            .ToList();

        var schema = ToolContractFixture.Wire(tool).GetProperty("inputSchema");
        var properties = schema.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var required = schema.TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal).ToList()
            : [];

        Assert.Equal(userParameters.Select(p => p.Name!).Order(StringComparer.Ordinal), properties);
        Assert.Equal(
            userParameters.Where(p => !p.HasDefaultValue).Select(p => p.Name!).Order(StringComparer.Ordinal),
            required);
    }

    // Literal pin of the published parameters: the reflection-based test above cannot catch a renamed
    // or dropped parameter, because it derives its expectation from the same method signature.
    public static TheoryData<string, string[], string[]> PublishedParameters => new()
    {
        { "analyze", ["filePath", "folderPath", "projectPath", "severities", "analyzers", "ruleIds", "limit"], [] },
        { "list_rules", ["projectPath", "copFilter", "analyzers", "verbose"], [] },
        { "get_fixes", ["projectPath", "filePath", "diagnosticId", "line", "column", "analyzers"],
            ["projectPath", "filePath", "diagnosticId", "line", "column"] },
        { "apply_fix", ["projectPath", "filePath", "diagnosticId", "line", "column", "equivalenceKey", "analyzers"],
            ["projectPath", "filePath", "diagnosticId", "line", "column", "equivalenceKey"] },
        { "apply_fix_all", ["projectPath", "diagnosticId", "scope", "filePath", "equivalenceKey", "analyzers", "dryRun"],
            ["projectPath", "diagnosticId"] },
    };

    [Theory]
    [MemberData(nameof(PublishedParameters))]
    public void InputSchema_PublishesThePinnedParameters(string name, string[] properties, string[] required)
    {
        var schema = ToolContractFixture.Wire(fixture.Tool(name)).GetProperty("inputSchema");

        Assert.Equal(properties, schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        var publishedRequired = schema.TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(e => e.GetString()!).ToList()
            : [];
        Assert.Equal(required.Order(StringComparer.Ordinal), publishedRequired.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryToolMethodInTheAssembly_IsPublicStatic()
    {
        var methods = typeof(McpHost).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToList();

        Assert.Equal(ExpectedNames.Length, methods.Count);
        Assert.All(methods, m => Assert.True(m.IsPublic && m.IsStatic,
            $"{m.DeclaringType?.FullName}.{m.Name} must be public static."));
        Assert.Equal(methods.Count, McpHost.NativeToolMethods().Count);
    }

    [Theory]
    [MemberData(nameof(Annotations))]
    public void TitleAndHints_MatchTheContract(string name, string title, bool readOnly, bool destructive, bool idempotent, bool openWorld)
    {
        var tool = fixture.Tool(name);

        Assert.Equal(title, tool.ProtocolTool.Title);
        var annotations = tool.ProtocolTool.Annotations;
        Assert.NotNull(annotations);
        Assert.Equal(readOnly, annotations.ReadOnlyHint);
        Assert.Equal(destructive, annotations.DestructiveHint);
        Assert.Equal(idempotent, annotations.IdempotentHint);
        Assert.Equal(openWorld, annotations.OpenWorldHint);

        // And on the wire: every hint is written out, none is left to the client's defaults.
        var wire = ToolContractFixture.Wire(tool);
        Assert.Equal(title, wire.GetProperty("title").GetString());
        var wireAnnotations = wire.GetProperty("annotations");
        Assert.Equal(readOnly, wireAnnotations.GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal(destructive, wireAnnotations.GetProperty("destructiveHint").GetBoolean());
        Assert.Equal(idempotent, wireAnnotations.GetProperty("idempotentHint").GetBoolean());
        Assert.Equal(openWorld, wireAnnotations.GetProperty("openWorldHint").GetBoolean());
    }

    // Pinned as literals on purpose: rewording one is a deliberate contract change, not a drift.
    [Theory]
    [InlineData("analyze", "al_compile hides warnings unless you remember onlyErrors=false, and al_getdiagnostics never runs analyzers.")]
    [InlineData("apply_fix", "Verify with analyze or al_compile (options.onlyErrors: false).")]
    [InlineData("apply_fix_all", "Verify with analyze or al_compile (options.onlyErrors: false).")]
    public void Description_ContainsPinnedSentence(string name, string sentence)
    {
        var description = fixture.Tool(name).ProtocolTool.Description;

        Assert.NotNull(description);
        Assert.Contains(NormalizeWhitespace(sentence), NormalizeWhitespace(description));
    }

    // The schema no longer carries "default", so the parameter description is the only place a
    // client learns a non-null default.
    [Theory]
    [MemberData(nameof(ToolNames))]
    public void ParameterDescriptions_StateEveryNonNullDefault(string name)
    {
        var parameters = ToolContractFixture.Method(fixture.Tool(name)).GetParameters()
            .Where(p => p.HasDefaultValue && p.DefaultValue is not null && p.ParameterType != typeof(CancellationToken));

        foreach (var parameter in parameters)
        {
            var description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.False(string.IsNullOrEmpty(description), $"{name}.{parameter.Name} has no description.");

            var value = parameter.DefaultValue is bool b
                ? (b ? "true" : "false")
                : Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture)!;

            // The value must sit right next to the word "default": "(default 500)", "Default: false.",
            // "'project' (default, ...". Both appearing somewhere in the text is not enough.
            var v = Regex.Escape(value);
            var statesDefault = Regex.IsMatch(description,
                $@"(?i)\bdefault\b\W{{0,4}}.{{0,12}}?'?{v}'?(?!\w)|(?<!\w)'?{v}'?\W{{0,3}}\(default\b");

            Assert.True(statesDefault,
                $"{name}.{parameter.Name}: description must state its default ({value}) next to the word 'default': \"{description}\"");
        }
    }

    [Fact]
    public void Analyze_RequiresNoArguments()
    {
        var schema = ToolContractFixture.Wire(fixture.Tool("analyze")).GetProperty("inputSchema");

        if (schema.TryGetProperty("required", out var required))
            Assert.Equal(0, required.GetArrayLength());
    }

    private static void CollectDefaultKeywords(JsonElement element, string path, List<string> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "default")
                        found.Add($"{path}.default");
                    CollectDefaultKeywords(property.Value, $"{path}.{property.Name}", found);
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                    CollectDefaultKeywords(item, $"{path}[{i++}]", found);
                break;
        }
    }

    private static string NormalizeWhitespace(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
