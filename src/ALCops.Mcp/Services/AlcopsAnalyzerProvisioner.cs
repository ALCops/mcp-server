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

internal sealed class AlcopsAnalyzerProvisioner
{
    private const string PackageId = "alcops.analyzers";
    private static readonly Uri IndexUri = new($"https://api.nuget.org/v3-flatcontainer/{PackageId}/index.json");

    private readonly BcToolsLocator _toolsLocator;
    private readonly AlcopsAnalyzersOption _option;
    private readonly HttpClient _httpClient;
    private readonly string _cacheRoot;
    private readonly ILogger<AlcopsAnalyzerProvisioner> _logger;
    private readonly TaskCompletionSource<string?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string?> Ready => _ready.Task;
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

        string? version;
        try
        {
            version = await ResolveVersionAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not reach NuGet to resolve ALCops analyzer version");
            return FallbackToCacheOrWarn(tfm);
        }

        if (version is null)
        {
            _logger.LogWarning("No suitable ALCops analyzer version found on NuGet");
            return FallbackToCacheOrWarn(tfm);
        }

        _logger.LogInformation("ALCops analyzers: resolved version {Version}", version);

        var cacheDir = Path.Combine(_cacheRoot, tfm, version);
        if (IsCacheValid(cacheDir))
        {
            _logger.LogInformation("ALCops analyzers: v{Version} ({Tfm}) from {Dir}", version, tfm, cacheDir);
            return cacheDir;
        }

        try
        {
            return await DownloadAndExtractAsync(version, tfm, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to download ALCops.Analyzers {Version}", version);
            return FallbackToCacheOrWarn(tfm);
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

        try
        {
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

            // Three outcomes: (1) move succeeds — temp dir becomes the cache entry,
            // (2) move fails, targetDir is valid — another process provisioned concurrently; discard temp, use targetDir,
            // (3) move fails, targetDir is invalid and undeletable — return temp dir for this session.
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            throw;
        }
    }

    private string? FallbackToCacheOrWarn(string tfm)
    {
        var cached = FindNewestCachedVersion(tfm);
        if (cached is not null)
        {
            _logger.LogWarning("ALCops analyzers: using cached version from {Dir}", cached);
            return cached;
        }

        _logger.LogWarning(
            "ALCops analyzers unavailable. To provision manually: " +
            "--alcops-analyzers <version> (when the NuGet index is reachable), or: " +
            "npx @alcops/core download --detect-using {ToolsDir} --output <dir>",
            _toolsLocator.ToolsDirectory);
        return null;
    }

    private string? FindNewestCachedVersion(string tfm)
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
