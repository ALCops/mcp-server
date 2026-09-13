using System.Runtime.InteropServices;
using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

public class DotnetHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"alcops-dotnet-host-{Guid.NewGuid():N}");

    private static readonly string ExeName =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreateFile(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "stub");
        return path;
    }

    [Fact]
    public void Resolve_DotnetHostPath_WinsOverEverythingElse()
    {
        var hostPath = CreateFile("host", ExeName);
        var dotnetRoot = Path.Combine(_root, "root");
        CreateFile("root", ExeName);

        var result = DotnetHost.Resolve(
            name => name == "DOTNET_HOST_PATH" ? hostPath : null,
            null,
            null);

        Assert.Equal(hostPath, result);
    }

    [Fact]
    public void Resolve_ProcessPath_WhenItIsDotnet()
    {
        var processPath = CreateFile("proc", ExeName);

        var result = DotnetHost.Resolve(_ => null, processPath, null);

        Assert.Equal(processPath, result);
    }

    [Fact]
    public void Resolve_ProcessPath_IgnoredWhenNotDotnet()
    {
        var processPath = CreateFile("proc", "alcops-mcp.exe");
        var rootDir = Path.Combine(_root, "root2");
        var dotnetInRoot = CreateFile("root2", ExeName);

        var result = DotnetHost.Resolve(
            name => name == "DOTNET_ROOT" ? rootDir : null,
            processPath,
            null);

        Assert.Equal(dotnetInRoot, result);
    }

    [Fact]
    public void Resolve_DotnetRoot_BeforePath()
    {
        var rootDir = Path.Combine(_root, "root3");
        var dotnetInRoot = CreateFile("root3", ExeName);
        var pathDir = Path.Combine(_root, "pathdir");
        CreateFile("pathdir", ExeName);
        var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ";" : ":";

        var result = DotnetHost.Resolve(
            name => name == "DOTNET_ROOT" ? rootDir : null,
            null,
            pathDir + sep + "/nonexistent");

        Assert.Equal(dotnetInRoot, result);
    }

    [Fact]
    public void Resolve_PathScan_FindsFirstMatch()
    {
        var dir1 = Path.Combine(_root, "dir1");
        CreateFile("dir1", ExeName);
        var dir2 = Path.Combine(_root, "dir2");
        CreateFile("dir2", ExeName);
        var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ";" : ":";

        var result = DotnetHost.Resolve(_ => null, null, dir1 + sep + dir2);

        Assert.Equal(Path.Combine(dir1, ExeName), result);
    }

    [Fact]
    public void Resolve_NothingFound_ReturnsBare()
    {
        var result = DotnetHost.Resolve(_ => null, null, "/nonexistent");

        Assert.Equal("dotnet", result);
    }
}
