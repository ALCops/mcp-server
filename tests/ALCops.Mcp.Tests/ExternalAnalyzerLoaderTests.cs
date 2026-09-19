using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

public class ExternalAnalyzerLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"alcops-loader-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreateDir(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateStubDll(string dir, string name)
    {
        File.WriteAllBytes(Path.Combine(dir, name), [0x4D, 0x5A]);
    }

    [Fact]
    public void ProvisionedFolder_WinsOverToolsFolder_ForAnalyzerFolderSpec()
    {
        var tools = CreateDir("tools");
        var provisioned = CreateDir("provisioned");

        CreateStubDll(tools, "ALCops.LinterCop.dll");
        CreateStubDll(provisioned, "ALCops.LinterCop.dll");

        var locator = new BcToolsLocator(tools);
        var loader = new ExternalAnalyzerLoader(locator, () => provisioned);

        var spec = AnalyzerSpec.Parse("${analyzerFolder}ALCops.LinterCop.dll");
        var result = loader.ResolveDllPath(spec, _root);

        Assert.Equal(Path.Combine(provisioned, "ALCops.LinterCop.dll"), result);
    }

    [Fact]
    public void ProvisionedFolder_FallsThrough_WhenFileAbsent()
    {
        var tools = CreateDir("tools");
        var provisioned = CreateDir("provisioned");

        CreateStubDll(tools, "ALCops.LinterCop.dll");
        // provisioned folder has nothing

        var locator = new BcToolsLocator(tools);
        var loader = new ExternalAnalyzerLoader(locator, () => provisioned);

        var spec = AnalyzerSpec.Parse("${analyzerFolder}ALCops.LinterCop.dll");
        var result = loader.ResolveDllPath(spec, _root);

        Assert.Equal(Path.Combine(tools, "ALCops.LinterCop.dll"), result);
    }

    [Fact]
    public void ProvisionedFolder_NotUsed_ForWellKnownBcCop()
    {
        var tools = CreateDir("tools");
        var provisioned = CreateDir("provisioned");

        CreateStubDll(tools, "Microsoft.Dynamics.Nav.CodeCop.dll");
        CreateStubDll(provisioned, "Microsoft.Dynamics.Nav.CodeCop.dll");

        var locator = new BcToolsLocator(tools);
        var loader = new ExternalAnalyzerLoader(locator, () => provisioned);

        var spec = AnalyzerSpec.Parse("${CodeCop}");
        var result = loader.ResolveDllPath(spec, _root);

        Assert.Equal(Path.Combine(tools, "Microsoft.Dynamics.Nav.CodeCop.dll"), result);
    }

    [Fact]
    public void NullProvisioner_FallsToToolsFolder()
    {
        var tools = CreateDir("tools");
        CreateStubDll(tools, "ALCops.LinterCop.dll");

        var locator = new BcToolsLocator(tools);
        var loader = new ExternalAnalyzerLoader(locator);

        var spec = AnalyzerSpec.Parse("${analyzerFolder}ALCops.LinterCop.dll");
        var result = loader.ResolveDllPath(spec, _root);

        Assert.Equal(Path.Combine(tools, "ALCops.LinterCop.dll"), result);
    }
}
