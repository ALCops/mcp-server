using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// The locator is the server's only remaining runtime dependency discovery, so its probe order and
/// its failure message are load-bearing. These tests use throwaway directories with a stub marker
/// DLL — nothing here needs a real BC install.
///
/// <para>All tests live in one class (xunit parallelises classes, not tests within a class) because
/// several of them mutate the process-wide BCDEVELOPMENTTOOLSPATH.</para>
/// </summary>
public class BcToolsLocatorTests : IDisposable
{
    private const string MarkerDll = "Microsoft.Dynamics.Nav.CodeAnalysis.dll";
    private const string EnvVar = "BCDEVELOPMENTTOOLSPATH";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"alcops-locator-test-{Guid.NewGuid():N}");
    private readonly string? _originalEnv = Environment.GetEnvironmentVariable(EnvVar);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, _originalEnv);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Creates a directory tree and drops a stub marker DLL in the deepest one.</summary>
    private string CreateToolsDir(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, MarkerDll), "stub");
        return path;
    }

    [Fact]
    public void Resolve_ExplicitPath_AcceptsFlatDirectory()
    {
        var tools = CreateToolsDir("flat");
        Assert.Equal(tools, BcToolsLocator.ResolveToolsDirectory(tools));
    }

    [Fact]
    public void Resolve_ExplicitPath_AcceptsNupkgToolsLayout()
    {
        // The dotnet tool / nupkg layout: <root>/tools/<tfm>/any/
        var tools = CreateToolsDir("pkg", "tools", "net8.0", "any");
        Assert.Equal(tools, BcToolsLocator.ResolveToolsDirectory(Path.Combine(_root, "pkg")));
    }

    [Fact]
    public void Resolve_ExplicitPath_PrefersNet10OverNet8()
    {
        CreateToolsDir("both", "net8.0");
        var net10 = CreateToolsDir("both", "net10.0");

        Assert.Equal(net10, BcToolsLocator.ResolveToolsDirectory(Path.Combine(_root, "both")));
    }

    [Fact]
    public void Resolve_ExplicitPathWithoutMarkerDll_ThrowsNamingThePath()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        var ex = Assert.Throws<InvalidOperationException>(() => BcToolsLocator.ResolveToolsDirectory(empty));
        Assert.Contains(empty, ex.Message);
    }

    [Fact]
    public void Resolve_ExplicitPath_WinsOverEnvironmentVariable()
    {
        var explicitDir = CreateToolsDir("explicit");
        Environment.SetEnvironmentVariable(EnvVar, CreateToolsDir("fromenv"));

        Assert.Equal(explicitDir, BcToolsLocator.ResolveToolsDirectory(explicitDir));
    }

    [Fact]
    public void Resolve_EnvironmentVariable_WinsOverToolStoreAndExtension()
    {
        // Whatever this machine has installed, an explicit env var must take precedence.
        var envDir = CreateToolsDir("env", "net8.0");
        Environment.SetEnvironmentVariable(EnvVar, Path.Combine(_root, "env"));

        Assert.Equal(envDir, BcToolsLocator.ResolveToolsDirectory());
    }

    [Fact]
    public void Resolve_UnusableEnvironmentVariable_FallsThroughInsteadOfThrowing()
    {
        // A stale env var pointing at a directory without the DLLs must not be fatal — the
        // tool store and AL extension are still worth probing.
        var stale = Path.Combine(_root, "stale");
        Directory.CreateDirectory(stale);
        Environment.SetEnvironmentVariable(EnvVar, stale);

        try
        {
            var resolved = BcToolsLocator.ResolveToolsDirectory();
            Assert.NotEqual(stale, resolved);
            Assert.True(File.Exists(Path.Combine(resolved, MarkerDll)));
        }
        catch (InvalidOperationException ex)
        {
            // No BC toolchain on this machine at all: the error must name the install command.
            Assert.Contains("dotnet tool install -g Microsoft.Dynamics.BusinessCentral.Development.Tools", ex.Message);
        }
    }

    [Fact]
    public void AnalyzerFolder_PrefersAnalyzersSubfolderWhenPresent()
    {
        var tools = CreateToolsDir("withanalyzers");
        var analyzers = Path.Combine(tools, "Analyzers");
        Directory.CreateDirectory(analyzers);

        Assert.Equal(analyzers, new BcToolsLocator(tools).AnalyzerFolder);
    }

    [Fact]
    public void AnalyzerFolder_FallsBackToFlatToolsDirectory()
    {
        var tools = CreateToolsDir("flatanalyzers");
        Assert.Equal(tools, new BcToolsLocator(tools).AnalyzerFolder);
    }

    [Fact]
    public void AlMcpPath_SitsBesideTheDevToolsDlls()
    {
        var tools = CreateToolsDir("almcp");
        var locator = new BcToolsLocator(tools);

        Assert.Equal(tools, Path.GetDirectoryName(locator.AlMcpPath));
        Assert.False(locator.HasAlMcp);

        // 16.2-and-earlier toolchains resolve fine but ship no almcp; 17.0+ do.
        File.WriteAllText(locator.AlMcpPath, "stub");
        Assert.True(locator.HasAlMcp);
    }
}
