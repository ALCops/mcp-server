using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ALCops.Mcp.Services;

public sealed class NuGetDevToolsDownloader
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alcops", "cache", "devtools");

    private string? _cachedToolsPath;

    /// <summary>
    /// Returns the path to a directory containing BC DevTools DLLs (cop analyzers, compiler, etc.).
    /// Checks .NET global tools cache first, then local cache, then downloads from NuGet.
    /// </summary>
    public async Task<string?> GetToolsPathAsync(CancellationToken ct = default)
    {
        if (_cachedToolsPath is not null)
            return _cachedToolsPath;

        // 1. Check .NET global tools cache (already installed via dotnet tool)
        var globalToolsPath = FindInDotnetToolsStore();
        if (globalToolsPath is not null)
            return _cachedToolsPath = globalToolsPath;

        // 2. Check local cache
        var localCached = FindInLocalCache();
        if (localCached is not null)
            return _cachedToolsPath = localCached;

        // 3. Download from NuGet
        try
        {
            var downloaded = await DownloadLatestAsync(ct);
            if (downloaded is not null)
                return _cachedToolsPath = downloaded;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to download BC DevTools from NuGet: {ex.Message}");
        }

        return null;
    }

    private static string? FindInDotnetToolsStore()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return null;

        var storeRoot = Path.Combine(home, ".dotnet", "tools", ".store");
        if (!Directory.Exists(storeRoot))
            return null;

        // Look for platform-specific and generic package names
        foreach (var packageName in GetPackageNames())
        {
            var packageDir = Path.Combine(storeRoot, packageName);
            if (!Directory.Exists(packageDir))
                continue;

            // Find highest version
            var bestVersion = FindHighestVersionDirectory(packageDir);
            if (bestVersion is null)
                continue;

            // The tools path is nested: <store>/<package>/<version>/<package>/<version>/tools/net8.0/any/
            var toolsPath = Path.Combine(packageDir, bestVersion, packageName, bestVersion, "tools", "net8.0", "any");
            if (Directory.Exists(toolsPath) && HasCopDlls(toolsPath))
                return toolsPath;
        }

        return null;
    }

    private static string? FindInLocalCache()
    {
        if (!Directory.Exists(CacheRoot))
            return null;

        var bestVersion = FindHighestVersionDirectory(CacheRoot);
        if (bestVersion is null)
            return null;

        var path = Path.Combine(CacheRoot, bestVersion);
        return HasCopDlls(path) ? path : null;
    }

    private static async Task<string?> DownloadLatestAsync(CancellationToken ct)
    {
        var packageName = GetPackageNames()[0]; // Primary (platform-specific)
        var version = await GetLatestStableVersionAsync(packageName, ct);
        if (version is null)
        {
            // Try generic package name
            packageName = "microsoft.dynamics.businesscentral.development.tools";
            version = await GetLatestStableVersionAsync(packageName, ct);
        }

        if (version is null)
        {
            Console.Error.WriteLine("Warning: Could not determine latest BC DevTools version from NuGet");
            return null;
        }

        var extractDir = Path.Combine(CacheRoot, version);
        if (Directory.Exists(extractDir) && HasCopDlls(extractDir))
            return extractDir;

        Console.Error.WriteLine($"Downloading BC DevTools {version} from NuGet...");

        var url = $"https://api.nuget.org/v3-flatcontainer/{packageName}/{version}/{packageName}.{version}.nupkg";
        using var response = await HttpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Directory.CreateDirectory(extractDir);

        // Extract DLLs from tools/net8.0/any/
        var prefix = "tools/net8.0/any/";
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrEmpty(entry.Name))
                continue; // Directory entry

            // Only extract DLL files
            if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;

            var destPath = Path.Combine(extractDir, entry.Name);
            entry.ExtractToFile(destPath, overwrite: true);
        }

        if (HasCopDlls(extractDir))
        {
            Console.Error.WriteLine($"BC DevTools {version} cached at {extractDir}");
            return extractDir;
        }

        // Cleanup failed extraction
        try { Directory.Delete(extractDir, true); } catch { }
        return null;
    }

    private static async Task<string?> GetLatestStableVersionAsync(string packageName, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.nuget.org/v3-flatcontainer/{packageName}/index.json";
            var json = await HttpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("versions", out var versions))
                return null;

            // Find latest stable version (no '-' in version string = stable)
            string? latest = null;
            foreach (var v in versions.EnumerateArray())
            {
                var ver = v.GetString();
                if (ver is not null && !ver.Contains('-'))
                    latest = ver;
            }

            return latest;
        }
        catch
        {
            return null;
        }
    }

    private static string[] GetPackageNames()
    {
        var platformSuffix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "linux";

        return
        [
            $"microsoft.dynamics.businesscentral.development.tools.{platformSuffix}",
            "microsoft.dynamics.businesscentral.development.tools"
        ];
    }

    private static string? FindHighestVersionDirectory(string parentDir)
    {
        string? bestDir = null;
        Version? bestVersion = null;

        try
        {
            foreach (var dir in Directory.GetDirectories(parentDir))
            {
                var name = Path.GetFileName(dir);
                // Version strings may have 4 parts: 16.2.28.57946
                if (Version.TryParse(name, out var version) && (bestVersion is null || version > bestVersion))
                {
                    bestVersion = version;
                    bestDir = name;
                }
            }
        }
        catch { }

        return bestDir;
    }

    private static bool HasCopDlls(string directory)
    {
        return File.Exists(Path.Combine(directory, "Microsoft.Dynamics.Nav.CodeCop.dll"));
    }
}
