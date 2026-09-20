using System.IO.Compression;
using System.Text.Json;
using ALCops.Mcp;
using Microsoft.Extensions.Logging;

namespace ALCops.Mcp.Services;

internal enum AlcopsAnalyzersMode { Latest, Prerelease, Pinned, Off }

internal sealed record AlcopsAnalyzersOption(AlcopsAnalyzersMode Mode, string? PinnedVersion = null)
{
    public static readonly AlcopsAnalyzersOption Latest = new(AlcopsAnalyzersMode.Latest);
    public static readonly AlcopsAnalyzersOption Prerelease = new(AlcopsAnalyzersMode.Prerelease);
    public static readonly AlcopsAnalyzersOption Off = new(AlcopsAnalyzersMode.Off);

    public static AlcopsAnalyzersOption Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return Latest;
        if (value.Equals("prerelease", StringComparison.OrdinalIgnoreCase))
            return Prerelease;
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return Off;
        return new AlcopsAnalyzersOption(AlcopsAnalyzersMode.Pinned, value);
    }
}

internal sealed class AlcopsAnalyzerProvisioner : IDisposable
{
    private const string PackageId = "alcops.analyzers";
    private static readonly Uri IndexUri = new($"https://api.nuget.org/v3-flatcontainer/{PackageId}/index.json");
    private const string InUseLockFileName = ".in-use";

    private readonly BcToolsLocator _toolsLocator;
    private readonly AlcopsAnalyzersOption _option;
    private readonly HttpClient _httpClient;
    private readonly string _cacheRoot;
    private readonly ILogger<AlcopsAnalyzerProvisioner> _logger;
    private readonly TaskCompletionSource<string?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FileStream? _inUseLock;
    private Task _backgroundRefresh = Task.CompletedTask;

    internal static readonly TimeSpan StaleTempDirectoryAge = TimeSpan.FromHours(1);

    public Task<string?> Ready => _ready.Task;

    /// <summary>
    /// The NuGet check/download that runs after <see cref="Ready"/> completed from cache.
    /// Never faults; <see cref="Task.CompletedTask"/> on cold-cache, pinned and off paths.
    /// Awaited by <see cref="ProvisionAsync"/> so shutdown waits for it.
    /// </summary>
    internal Task BackgroundRefresh => _backgroundRefresh;
    internal TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(10);
    internal TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public AlcopsAnalyzerProvisioner(
        BcToolsLocator toolsLocator,
        AlcopsAnalyzersOption option,
        HttpMessageHandler? httpHandler,
        string? cacheRoot,
        ILogger<AlcopsAnalyzerProvisioner> logger)
    {
        _toolsLocator = toolsLocator;
        _option = option;
        _httpClient = httpHandler is not null ? new HttpClient(httpHandler) : new HttpClient();
        _cacheRoot = cacheRoot
            ?? Environment.GetEnvironmentVariable("ALCOPS_ANALYZERS_CACHE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alcops", "analyzers");
        _logger = logger;
    }

    public void Dispose()
    {
        _inUseLock?.Dispose();
        _inUseLock = null;
        _httpClient.Dispose();
    }

    // FileShare.None is a cross-process lock via flock on Unix; reliable on local file systems,
    // advisory on network mounts such as NFS home directories.
    private static FileStream AcquireInUseLock(string dir) =>
        new(Path.Combine(dir, InUseLockFileName), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);

    public async Task ProvisionAsync(CancellationToken ct)
    {
        string? result = null;
        try
        {
            result = await ProvisionCoreAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ALCops analyzer provisioning failed");
        }

        _ready.TrySetResult(result);
        await _backgroundRefresh;
    }

    private async Task<string?> ProvisionCoreAsync(CancellationToken ct)
    {
        if (_option.Mode == AlcopsAnalyzersMode.Off)
        {
            _logger.LogInformation("ALCops analyzer provisioning disabled (--alcops-analyzers off)");
            return null;
        }

        string tfm;
        try
        {
            tfm = TargetFrameworkMoniker.DetectDevToolsTfm(_toolsLocator.ToolsDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not detect DevTools target framework");
            return null;
        }

        _logger.LogInformation("DevTools target framework: {Tfm}", tfm);

        SweepStaleTempDirectories();
        ct.ThrowIfCancellationRequested();

        if (_option.Mode is AlcopsAnalyzersMode.Latest or AlcopsAnalyzersMode.Prerelease)
        {
            var cached = FindNewestCachedVersion(tfm, includePrerelease: _option.Mode == AlcopsAnalyzersMode.Prerelease);
            if (cached is not null)
            {
                _logger.LogInformation(
                    "ALCops analyzers: v{Version} ({Tfm}) from cache {Dir}; checking NuGet in the background",
                    Path.GetFileName(cached), tfm, cached);
                _backgroundRefresh = RefreshCacheAsync(tfm, cached, ct);
                return cached;
            }
        }

        return await FetchOrFallbackAsync(tfm, ct);
    }

    private async Task<string?> FetchAsync(string tfm, CancellationToken ct)
    {
        var version = await ResolveVersionAsync(ct);
        if (version is null)
            return null;

        _logger.LogInformation("ALCops analyzers: resolved version {Version}", version);

        var cacheDir = Path.Combine(_cacheRoot, tfm, version);
        if (IsCacheValid(cacheDir))
        {
            _logger.LogInformation("ALCops analyzers: v{Version} ({Tfm}) from {Dir}", version, tfm, cacheDir);
            return cacheDir;
        }

        return await DownloadAndExtractAsync(version, tfm, ct);
    }

    private async Task<string?> FetchOrFallbackAsync(string tfm, CancellationToken ct)
    {
        try
        {
            var result = await FetchAsync(tfm, ct);
            if (result is not null)
                return result;

            _logger.LogWarning("No suitable ALCops analyzer version found on NuGet");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ALCops analyzers: NuGet provisioning failed");
        }

        return FallbackToCacheOrWarn(tfm);
    }

    private async Task RefreshCacheAsync(string tfm, string current, CancellationToken ct)
    {
        try
        {
            var result = await FetchAsync(tfm, ct);
            if (result is not null && !string.Equals(result, current, StringComparison.OrdinalIgnoreCase))
                _logger.LogInformation(
                    "ALCops analyzers: fetched v{Version} ({Tfm}); it will be used on next start",
                    Path.GetFileName(result), tfm);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ALCops analyzers: background refresh failed; keeping v{Version}",
                Path.GetFileName(current));
        }
    }

    private async Task<string?> ResolveVersionAsync(CancellationToken ct)
    {
        if (_option.Mode == AlcopsAnalyzersMode.Pinned)
            return _option.PinnedVersion;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ResolveTimeout);
        try
        {
            var response = await _httpClient.GetAsync(IndexUri, cts.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cts.Token);

            var (latest, prerelease) = NuGetVersions.Parse(json);
            return _option.Mode == AlcopsAnalyzersMode.Prerelease ? prerelease : latest;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"NuGet index request timed out after {ResolveTimeout.TotalSeconds:0}s", ex);
        }
    }

    private async Task<string> DownloadAndExtractAsync(string version, string tfm, CancellationToken ct)
    {
        var nupkgUrl = $"https://api.nuget.org/v3-flatcontainer/{PackageId}/{version}/{PackageId}.{version}.nupkg";
        _logger.LogInformation("Downloading {Url}", nupkgUrl);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DownloadTimeout);
        try
        {
            using var response = await _httpClient.GetAsync(nupkgUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            var tempFile = Path.Combine(Path.GetTempPath(), $"alcops-{version}-{Guid.NewGuid():N}.nupkg");
            try
            {
                await using (var fs = File.Create(tempFile))
                await using (var content = await response.Content.ReadAsStreamAsync(cts.Token))
                    await content.CopyToAsync(fs, cts.Token);

                ct.ThrowIfCancellationRequested();
                return ExtractPackage(tempFile, version, tfm, nupkgUrl);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Download of ALCops.Analyzers {version} timed out after {DownloadTimeout.TotalSeconds:0}s", ex);
        }
    }

    internal string ExtractPackage(string nupkgPath, string version, string tfm, string sourceUrl)
    {
        using var zip = ZipFile.OpenRead(nupkgPath);

        var libFolders = zip.Entries
            .Select(e => e.FullName)
            .Where(n => n.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                     && n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                     && n.Split('/').Length >= 3)
            .Select(n => n.Split('/')[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var bestTfm = TargetFrameworkMoniker.FindBestLibFolder(libFolders, tfm)
            ?? throw new InvalidOperationException(
                $"ALCops.Analyzers {version} has no compatible TFM for {tfm}. Available: {string.Join(", ", libFolders)}");

        if (!bestTfm.Equals(tfm, StringComparison.OrdinalIgnoreCase))
            _logger.LogWarning("ALCops analyzers: no {Requested} in package; using {Actual} fallback", tfm, bestTfm);

        var targetDir = Path.Combine(_cacheRoot, tfm, version);
        var tempDir = $"{targetDir}.tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(tempDir);

        FileStream? tempLock = null;
        try
        {
            tempLock = AcquireInUseLock(tempDir);

            var prefix = $"lib/{bestTfm}/";
            var files = new List<string>();

            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Length <= prefix.Length)
                    continue;
                if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileName = Path.GetFileName(entry.FullName);
                entry.ExtractToFile(Path.Combine(tempDir, fileName), overwrite: true);
                files.Add(fileName);
            }

            if (files.Count == 0)
                throw new InvalidOperationException($"No DLLs in lib/{bestTfm}/ of ALCops.Analyzers {version}");

            var manifest = new
            {
                alcopsVersion = version,
                requestedTfm = tfm,
                targetFramework = bestTfm,
                downloadedAt = DateTime.UtcNow.ToString("o"),
                files = files.Order().ToArray(),
                source = sourceUrl
            };

            File.WriteAllText(
                Path.Combine(tempDir, ".alcops-manifest.json"),
                JsonSerializer.Serialize(manifest, JsonDefaults.Options));

            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);

            if (Directory.Exists(targetDir) && !IsCacheValid(targetDir))
            {
                _logger.LogInformation("Replacing incomplete cache directory {Dir}", targetDir);
                try { Directory.Delete(targetDir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "ALCops analyzers: could not delete incomplete cache directory {Dir}", targetDir);
                }
            }

            tempLock.Dispose();
            tempLock = null;
            try { File.Delete(Path.Combine(tempDir, InUseLockFileName)); } catch { }

            try
            {
                Directory.Move(tempDir, targetDir);
            }
            catch (IOException) when (Directory.Exists(targetDir))
            {
                if (IsCacheValid(targetDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                    _logger.LogInformation("v{Version} was provisioned concurrently; using {Dir}", version, targetDir);
                }
                else
                {
                    if (!Directory.Exists(tempDir) || !IsCacheValid(tempDir))
                        throw new IOException($"ALCops analyzers: {tempDir} disappeared before it could be used");
                    _inUseLock = AcquireInUseLock(tempDir);
                    _logger.LogWarning(
                        "ALCops analyzers: {TargetDir} is incomplete and could not be replaced; using {TempDir} for this session",
                        targetDir, tempDir);
                    return tempDir;
                }
            }

            _logger.LogInformation("ALCops analyzers: v{Version} ({Tfm}) provisioned to {Dir}", version, bestTfm, targetDir);
            return targetDir;
        }
        catch
        {
            tempLock?.Dispose();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Falls back to the newest cached version, including prereleases: loading a prerelease
    /// with a warning beats running without ALCops rules when NuGet is unreachable.
    /// </summary>
    private string? FallbackToCacheOrWarn(string tfm)
    {
        var cached = FindNewestCachedVersion(tfm);
        if (cached is not null)
        {
            if (_option.Mode == AlcopsAnalyzersMode.Latest
                && SemanticVersion.TryParse(Path.GetFileName(cached), out var v) && !v.IsStable)
            {
                _logger.LogWarning(
                    "ALCops analyzers: NuGet unreachable and no stable version cached; using prerelease v{Version} from {Dir} as a last resort",
                    Path.GetFileName(cached), cached);
            }
            else
            {
                _logger.LogWarning("ALCops analyzers: using cached version from {Dir}", cached);
            }
            return cached;
        }

        _logger.LogWarning(
            "ALCops analyzers unavailable. To provision manually: " +
            "--alcops-analyzers <version> (when the NuGet index is reachable), or: " +
            "npx @alcops/core download --detect-using {ToolsDir} --output <dir>",
            _toolsLocator.ToolsDirectory);
        return null;
    }

    private string? FindNewestCachedVersion(string tfm, bool includePrerelease = true)
    {
        var tfmDir = Path.Combine(_cacheRoot, tfm);
        if (!Directory.Exists(tfmDir))
            return null;

        string? best = null;
        SemanticVersion? bestVersion = null;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(tfmDir))
            {
                var name = Path.GetFileName(dir);
                if (name.Contains(".tmp-", StringComparison.Ordinal))
                    continue;
                if (!IsCacheValid(dir))
                    continue;
                if (!SemanticVersion.TryParse(name, out var v))
                    continue;
                if (!includePrerelease && !v.IsStable)
                    continue;

                if (bestVersion is null || v.CompareTo(bestVersion) > 0)
                {
                    best = dir;
                    bestVersion = v;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return best;
    }

    // Housekeeping only — must never influence Ready.
    private void SweepStaleTempDirectories()
    {
        try
        {
            if (!Directory.Exists(_cacheRoot))
                return;

            var count = 0;
            foreach (var tfmDir in BcToolsLocator.SafeEnumerateDirectories(_cacheRoot))
            {
                foreach (var dir in BcToolsLocator.SafeEnumerateDirectories(tfmDir, "*.tmp-*"))
                {
                    try
                    {
                        if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < StaleTempDirectoryAge)
                            continue;

                        try { using var probe = AcquireInUseLock(dir); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _logger.LogDebug("ALCops analyzers: skipping in-use extraction directory {Dir}", dir);
                            continue;
                        }

                        Directory.Delete(dir, recursive: true);
                        count++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "ALCops analyzers: could not remove stale extraction directory {Dir}", dir);
                    }
                }
            }

            if (count > 0)
                _logger.LogInformation("ALCops analyzers: removed {Count} stale extraction directories", count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "ALCops analyzers: sweep of stale extraction directories aborted");
        }
    }

    internal static bool IsCacheValid(string cacheDir)
    {
        var manifestPath = Path.Combine(cacheDir, ".alcops-manifest.json");
        if (!File.Exists(manifestPath))
            return false;

        try
        {
            var json = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var file in files.EnumerateArray())
            {
                var fileName = file.GetString();
                if (fileName is null || !File.Exists(Path.Combine(cacheDir, fileName)))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
