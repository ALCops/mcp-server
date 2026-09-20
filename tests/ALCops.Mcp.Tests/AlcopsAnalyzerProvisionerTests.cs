using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using ALCops.Mcp;
using ALCops.Mcp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ALCops.Mcp.Tests;

public class AlcopsAnalyzerProvisionerTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(
        Path.GetTempPath(), $"alcops-provisioner-test-{Guid.NewGuid():N}");

    private static readonly string Tfm =
        TargetFrameworkMoniker.DetectDevToolsTfm(TestAnalyzers.ToolsLocator.ToolsDirectory);

    private const string IndexUrl = "https://api.nuget.org/v3-flatcontainer/alcops.analyzers/index.json";

    private static readonly string IndexJson =
        """{"versions":["0.4.1--no-branch-.1","1.0.0","1.1.0","1.2.0","1.3.0-preview.1"]}""";

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);
    }

    private static string NupkgUrl(string v) =>
        $"https://api.nuget.org/v3-flatcontainer/alcops.analyzers/{v}/alcops.analyzers.{v}.nupkg";

    private static void WriteManifest(string dir, string version, IEnumerable<string> files)
    {
        var manifest = new
        {
            alcopsVersion = version,
            requestedTfm = Tfm,
            targetFramework = Tfm,
            downloadedAt = DateTime.UtcNow.ToString("o"),
            files = files.Order().ToArray(),
            source = "test"
        };

        File.WriteAllText(
            Path.Combine(dir, ".alcops-manifest.json"),
            JsonSerializer.Serialize(manifest, JsonDefaults.Options));
    }

    private string SeedCache(string version, params string[] files)
    {
        if (files.Length == 0)
            files = ["ALCops.Fake.dll"];

        var dir = Path.Combine(_cacheRoot, Tfm, version);
        Directory.CreateDirectory(dir);

        foreach (var file in files)
            File.WriteAllBytes(Path.Combine(dir, file), [0x4D, 0x5A]);

        WriteManifest(dir, version, files);

        return dir;
    }

    private string SeedInvalidCache(string version)
    {
        var dir = Path.Combine(_cacheRoot, Tfm, version);
        Directory.CreateDirectory(dir);

        WriteManifest(dir, version, ["ALCops.Fake.dll"]);

        return dir;
    }

    private static byte[] BuildFakeNupkg(params (string tfm, string fileName)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (tfm, fileName) in entries)
            {
                var entry = zip.CreateEntry($"lib/{tfm}/{fileName}");
                using var stream = entry.Open();
                stream.Write([0x4D, 0x5A]);
            }
        }

        return ms.ToArray();
    }

    private AlcopsAnalyzerProvisioner Create(AlcopsAnalyzersOption option, FakeHandler handler) =>
        new(TestAnalyzers.ToolsLocator, option, handler, _cacheRoot,
            NullLogger<AlcopsAnalyzerProvisioner>.Instance);

    private AlcopsAnalyzerProvisioner Create(AlcopsAnalyzersOption option, FakeHandler handler, ILogger<AlcopsAnalyzerProvisioner> logger) =>
        new(TestAnalyzers.ToolsLocator, option, handler, _cacheRoot, logger);

    [Fact]
    public async Task Provision_ExtractsCorrectTfm_And_WritesManifest()
    {
        var handler = new FakeHandler();
        handler.Respond(IndexUrl, IndexJson);
        handler.Respond(NupkgUrl("1.2.0"),
            BuildFakeNupkg(($"{Tfm}", "ALCops.Fake.dll"), ("net8.0", "ALCops.Fake.dll")));

        var provisioner = Create(AlcopsAnalyzersOption.Latest, handler);
        await provisioner.ProvisionAsync(CancellationToken.None);
        var result = await provisioner.Ready;

        Assert.NotNull(result);
        Assert.True(File.Exists(Path.Combine(result, "ALCops.Fake.dll")));
        Assert.True(File.Exists(Path.Combine(result, ".alcops-manifest.json")));

        var manifest = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(result, ".alcops-manifest.json")));
        Assert.Equal("1.2.0", manifest.RootElement.GetProperty("alcopsVersion").GetString());
        Assert.Equal(Tfm, manifest.RootElement.GetProperty("requestedTfm").GetString());

        Assert.True(provisioner.BackgroundRefresh.IsCompleted);
    }

    [Fact]
    public async Task Provision_SecondRun_HitsCache_OnlyIndexRequested()
    {
        var handler1 = new FakeHandler();
        handler1.Respond(IndexUrl, IndexJson);
        handler1.Respond(NupkgUrl("1.2.0"),
            BuildFakeNupkg(($"{Tfm}", "ALCops.Fake.dll")));

        var p1 = Create(AlcopsAnalyzersOption.Latest, handler1);
        await p1.ProvisionAsync(CancellationToken.None);
        Assert.NotNull(await p1.Ready);

        var handler2 = new FakeHandler();
        handler2.Respond(IndexUrl, IndexJson);

        var p2 = Create(AlcopsAnalyzersOption.Latest, handler2);
        await p2.ProvisionAsync(CancellationToken.None);
        var result = await p2.Ready;

        Assert.NotNull(result);
        Assert.Single(handler2.RequestUrls);
        Assert.Equal(IndexUrl, handler2.RequestUrls[0]);
    }

    [Fact]
    public async Task Provision_NetworkFailure_WarmCache_ReturnsCached()
    {
        var handler1 = new FakeHandler();
        handler1.Respond(IndexUrl, IndexJson);
        handler1.Respond(NupkgUrl("1.2.0"),
            BuildFakeNupkg(($"{Tfm}", "ALCops.Fake.dll")));

        var p1 = Create(AlcopsAnalyzersOption.Latest, handler1);
        await p1.ProvisionAsync(CancellationToken.None);
        var firstResult = await p1.Ready;
        Assert.NotNull(firstResult);

        var handler2 = new FakeHandler { ThrowOnRequest = true };
        var p2 = Create(AlcopsAnalyzersOption.Latest, handler2);
        await p2.ProvisionAsync(CancellationToken.None);
        var result = await p2.Ready;

        Assert.NotNull(result);
        Assert.Equal(firstResult, result);
    }

    [Fact]
    public async Task Provision_NetworkFailure_ColdCache_ReturnsNull()
    {
        var handler = new FakeHandler { ThrowOnRequest = true };
        var provisioner = Create(AlcopsAnalyzersOption.Latest, handler);
        await provisioner.ProvisionAsync(CancellationToken.None);
        var result = await provisioner.Ready;

        Assert.Null(result);
    }

    [Fact]
    public async Task Provision_Off_ZeroRequests()
    {
        var handler = new FakeHandler();
        var provisioner = Create(AlcopsAnalyzersOption.Off, handler);
        await provisioner.ProvisionAsync(CancellationToken.None);
        var result = await provisioner.Ready;

        Assert.Null(result);
        Assert.Empty(handler.RequestUrls);
    }

    [Fact]
    public async Task Provision_Pinned_NoIndexRequest()
    {
        var handler = new FakeHandler();
        handler.Respond(NupkgUrl("1.1.0"),
            BuildFakeNupkg(($"{Tfm}", "ALCops.Fake.dll")));

        var provisioner = Create(
            AlcopsAnalyzersOption.Parse("1.1.0"), handler);
        await provisioner.ProvisionAsync(CancellationToken.None);
        var result = await provisioner.Ready;

        Assert.NotNull(result);
        Assert.DoesNotContain(IndexUrl, handler.RequestUrls);
        Assert.Contains(NupkgUrl("1.1.0"), handler.RequestUrls);
    }

    [Fact]
    public async Task Provision_IndexTimeout_WarmCache_ReturnsCached()
    {
        var cached = SeedCache("1.1.0");

        var handler = new FakeHandler();
        handler.Delays[IndexUrl] = TimeSpan.FromSeconds(5);
        handler.Respond(IndexUrl, IndexJson);

        var provisioner = new AlcopsAnalyzerProvisioner(
            TestAnalyzers.ToolsLocator, AlcopsAnalyzersOption.Latest, handler, _cacheRoot,
            NullLogger<AlcopsAnalyzerProvisioner>.Instance)
        {
            ResolveTimeout = TimeSpan.FromMilliseconds(100)
        };

        var sw = Stopwatch.StartNew();
        await provisioner.ProvisionAsync(CancellationToken.None);
        var result = await provisioner.Ready;
        sw.Stop();

        Assert.Equal(cached, result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Provision_DownloadTimeout_WarmCache_ReturnsCached()
    {
        var cached = SeedCache("1.1.0");

        var handler = new FakeHandler();
        handler.Respond(IndexUrl, IndexJson);
        handler.Delays[NupkgUrl("1.2.0")] = TimeSpan.FromSeconds(5);
        handler.Respond(NupkgUrl("1.2.0"),
            BuildFakeNupkg(($"{Tfm}", "ALCops.Fake.dll")));

        var provisioner = new AlcopsAnalyzerProvisioner(
            TestAnalyzers.ToolsLocator, AlcopsAnalyzersOption.Latest, handler, _cacheRoot,
            NullLogger<AlcopsAnalyzerProvisioner>.Instance)
        {
            DownloadTimeout = TimeSpan.FromMilliseconds(100)
        };

        await provisioner.ProvisionAsync(CancellationToken.None);
        var result = await provisioner.Ready;

        Assert.Equal(cached, result);
    }

    [Fact]
    public async Task Provision_HalfExtractedCacheDir_IsReplaced()
    {
        SeedInvalidCache("1.2.0");

        var handler = new FakeHandler();
        handler.Respond(IndexUrl, IndexJson);
        handler.Respond(NupkgUrl("1.2.0"),
            BuildFakeNupkg(($"{Tfm}", "ALCops.Fake.dll")));

        var provisioner = Create(AlcopsAnalyzersOption.Latest, handler);
        await provisioner.ProvisionAsync(CancellationToken.None);
        var result = await provisioner.Ready;

        Assert.NotNull(result);
        Assert.True(AlcopsAnalyzerProvisioner.IsCacheValid(result));
        Assert.True(File.Exists(Path.Combine(result, "ALCops.Fake.dll")));

        var tfmDir = Path.Combine(_cacheRoot, Tfm);
        var tmpDirs = Directory.EnumerateDirectories(tfmDir, "*.tmp-*");
        Assert.Empty(tmpDirs);
    }

    [Fact]
    public void ExtractPackage_TargetAlreadyValid_KeepsExistingCopy()
    {
        var dir = SeedCache("1.2.0");
        var dllPath = Path.Combine(dir, "ALCops.Fake.dll");
        var oldBytes = File.ReadAllBytes(dllPath);

        var nupkgPath = Path.Combine(Path.GetTempPath(), $"alcops-test-{Guid.NewGuid():N}.nupkg");
        try
        {
            File.WriteAllBytes(nupkgPath, BuildFakeNupkg(($"{Tfm}", "ALCops.Fake.dll")));

            var handler = new FakeHandler();
            var provisioner = Create(AlcopsAnalyzersOption.Latest, handler);
            var result = provisioner.ExtractPackage(nupkgPath, "1.2.0", Tfm, "test");

            Assert.Equal(dir, result);
            Assert.Equal(oldBytes, File.ReadAllBytes(dllPath));

            var tfmDir = Path.Combine(_cacheRoot, Tfm);
            var tmpDirs = Directory.EnumerateDirectories(tfmDir, "*.tmp-*");
            Assert.Empty(tmpDirs);
        }
        finally
        {
            try { File.Delete(nupkgPath); } catch { }
        }
    }

    [Fact]
    public async Task Provision_CallerCancelled_ReadyIsNull_EvenWithCache()
    {
        SeedCache("1.1.0");

        var handler = new FakeHandler();
        handler.Respond(IndexUrl, IndexJson);

        var provisioner = Create(AlcopsAnalyzersOption.Latest, handler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await provisioner.ProvisionAsync(cts.Token);
        var result = await provisioner.Ready;

        Assert.Null(result);
    }

    [Fact]
    public async Task Fallback_PicksNewestCachedPrereleaseNumerically()
    {
        SeedCache("1.3.0-preview.9");
        SeedCache("1.3.0-preview.10");

        var handler = new FakeHandler { ThrowOnRequest = true };
        var provisioner = Create(AlcopsAnalyzersOption.Latest, handler);
        await provisioner.ProvisionAsync(CancellationToken.None);
        var result = await provisioner.Ready;

        Assert.NotNull(result);
        Assert.EndsWith("preview.10", Path.GetFileName(result));
    }

    [Fact]
    public async Task Fallback_SkipsInvalidAndTmpDirs()
    {
        SeedCache("1.1.0");
        SeedInvalidCache("1.2.0");

        var tmpDir = Path.Combine(_cacheRoot, Tfm, "1.3.0.tmp-abc");
        Directory.CreateDirectory(tmpDir);
        var manifest = new
        {
            alcopsVersion = "1.3.0",
            requestedTfm = Tfm,
            targetFramework = Tfm,
            downloadedAt = DateTime.UtcNow.ToString("o"),
            files = new[] { "ALCops.Fake.dll" },
            source = "test"
        };
        File.WriteAllBytes(Path.Combine(tmpDir, "ALCops.Fake.dll"), [0x4D, 0x5A]);
        File.WriteAllText(
            Path.Combine(tmpDir, ".alcops-manifest.json"),
            JsonSerializer.Serialize(manifest, JsonDefaults.Options));

        var handler = new FakeHandler { ThrowOnRequest = true };
        var provisioner = Create(AlcopsAnalyzersOption.Latest, handler);
        await provisioner.ProvisionAsync(CancellationToken.None);
        var result = await provisioner.Ready;

        Assert.NotNull(result);
        Assert.EndsWith("1.1.0", Path.GetFileName(result));
    }

    [Fact]
    public void ExtractPackage_TargetInvalidAndUndeletable_ReturnsTempDir()
    {
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
            return;

        var invalidDir = SeedInvalidCache("1.2.0");

        var nupkgPath = Path.Combine(Path.GetTempPath(), $"alcops-test-{Guid.NewGuid():N}.nupkg");
        try
        {
            File.WriteAllBytes(nupkgPath, BuildFakeNupkg(($"{Tfm}", "ALCops.Fake.dll")));

            using var dirLock = DirectoryLock.Create(invalidDir);

            var logger = new CapturingLogger();
            var handler = new FakeHandler();
            using var provisioner = Create(AlcopsAnalyzersOption.Latest, handler, logger);
            var result = provisioner.ExtractPackage(nupkgPath, "1.2.0", Tfm, "test");

            Assert.NotEqual(invalidDir, result);
            Assert.StartsWith($"{invalidDir}.tmp-", result);
            Assert.True(AlcopsAnalyzerProvisioner.IsCacheValid(result));
            Assert.True(File.Exists(Path.Combine(result, "ALCops.Fake.dll")));
            Assert.Contains(logger.Entries, e =>
                e.Level == LogLevel.Warning && e.Message.Contains("could not be replaced"));

            var lockFilePath = Path.Combine(result, ".in-use");
            Assert.True(File.Exists(lockFilePath));
            Assert.Throws<IOException>(() =>
                new FileStream(lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose());

            provisioner.Dispose();
            using var released = new FileStream(lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            try { File.Delete(nupkgPath); } catch { }
        }
    }

    [Fact]
    public async Task Provision_SweepsStaleTempDirectories_KeepsFreshOnes()
    {
        var staleDir = Path.Combine(_cacheRoot, Tfm, "1.2.0.tmp-old");
        Directory.CreateDirectory(staleDir);
        Directory.SetLastWriteTimeUtc(staleDir, DateTime.UtcNow.AddHours(-2));

        var freshDir = Path.Combine(_cacheRoot, Tfm, "1.2.0.tmp-new");
        Directory.CreateDirectory(freshDir);

        SeedCache("1.1.0");

        var handler = new FakeHandler { ThrowOnRequest = true };
        var provisioner = Create(AlcopsAnalyzersOption.Latest, handler);
        await provisioner.ProvisionAsync(CancellationToken.None);
        var result = await provisioner.Ready;

        Assert.False(Directory.Exists(staleDir));
        Assert.True(Directory.Exists(freshDir));
        Assert.NotNull(result);
        Assert.EndsWith("1.1.0", Path.GetFileName(result));
    }

    [Fact]
    public async Task Sweep_SkipsTempDirectoryHeldByAnotherProcess()
    {
        var heldDir = Path.Combine(_cacheRoot, Tfm, "1.2.0.tmp-held");
        Directory.CreateDirectory(heldDir);
        Directory.SetLastWriteTimeUtc(heldDir, DateTime.UtcNow.AddHours(-2));

        FileStream? holdLock = null;
        try
        {
            holdLock = new FileStream(
                Path.Combine(heldDir, ".in-use"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);

            SeedCache("1.1.0");

            var handler = new FakeHandler { ThrowOnRequest = true };
            using var provisioner = Create(AlcopsAnalyzersOption.Latest, handler);
            await provisioner.ProvisionAsync(CancellationToken.None);
            var result = await provisioner.Ready;

            Assert.True(Directory.Exists(heldDir));
            Assert.NotNull(result);
            Assert.EndsWith("1.1.0", Path.GetFileName(result));
        }
        finally
        {
            holdLock?.Dispose();
        }
    }

    [Fact]
    public async Task Sweep_CoversEveryTfmFolder()
    {
        var otherTfmDir = Path.Combine(_cacheRoot, "net8.0", "1.0.0.tmp-old");
        Directory.CreateDirectory(otherTfmDir);
        Directory.SetLastWriteTimeUtc(otherTfmDir, DateTime.UtcNow.AddHours(-2));

        SeedCache("1.1.0");

        var handler = new FakeHandler { ThrowOnRequest = true };
        using var provisioner = Create(AlcopsAnalyzersOption.Latest, handler);
        await provisioner.ProvisionAsync(CancellationToken.None);

        Assert.False(Directory.Exists(otherTfmDir));
    }

    [Fact]
    public async Task Sweep_EnumerationFailure_DoesNotFailProvisioning()
    {
        var cached = SeedCache("1.1.0");

        // A stale *.tmp-* dir with a locked file: the sweep acquires the .in-use
        // probe successfully but Directory.Delete throws IOException, exercising
        // the per-directory catch on every platform.
        var staleDir = Path.Combine(_cacheRoot, Tfm, "1.0.0.tmp-locked");
        Directory.CreateDirectory(staleDir);
        Directory.SetLastWriteTimeUtc(staleDir, DateTime.UtcNow.AddHours(-2));
        var lockFile = Path.Combine(staleDir, "locked.bin");
        File.WriteAllBytes(lockFile, [0x00]);

        // A second TFM folder with a stale *.tmp-* dir that must be swept even
        // when another TFM folder's enumeration fails or a directory in the first
        // folder cannot be deleted. Named so neither alphabetical ordering is
        // assumed — per-folder isolation makes both orders pass.
        var sweepableStaleDir = Path.Combine(_cacheRoot, "other-tfm", "1.0.0.tmp-sweepable");
        Directory.CreateDirectory(sweepableStaleDir);
        Directory.SetLastWriteTimeUtc(sweepableStaleDir, DateTime.UtcNow.AddHours(-2));

        FileStream? holdLock = null;
        string? unreadableDir = null;
        try
        {
            holdLock = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.None);

            // On Unix (non-root): make a TFM folder unreadable so the lazy enumerator
            // inside SafeEnumerateDirectories throws on the first MoveNext, exercising
            // the per-TFM-folder catch. On Windows, lazy-enumeration failures from
            // concurrent deletions cannot be triggered deterministically; only the
            // delete-failure and cross-folder-continuation cases are tested there.
            if (!OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess)
            {
                unreadableDir = Path.Combine(_cacheRoot, "unreadable-tfm");
                Directory.CreateDirectory(unreadableDir);
#pragma warning disable CA1416
                File.SetUnixFileMode(unreadableDir, UnixFileMode.None);
#pragma warning restore CA1416
            }

            var handler = new FakeHandler { ThrowOnRequest = true };
            using var provisioner = Create(AlcopsAnalyzersOption.Latest, handler);
            await provisioner.ProvisionAsync(CancellationToken.None);
            var result = await provisioner.Ready;

            Assert.NotNull(result);
            Assert.Equal(cached, result);
            Assert.True(Directory.Exists(staleDir), "Locked directory should survive the sweep");
            Assert.False(Directory.Exists(sweepableStaleDir),
                "Stale dir in a separate TFM folder must still be swept");
        }
        finally
        {
            holdLock?.Dispose();
            if (unreadableDir is not null)
            {
#pragma warning disable CA1416
                File.SetUnixFileMode(unreadableDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
            }
        }
    }

    [Fact]
    public async Task Provision_WarmCache_ReadyFromCache_RefreshDownloadsNewerForNextStart()
    {
        var dir110 = SeedCache("1.1.0");

        var handler = new FakeHandler { Gate = new TaskCompletionSource() };
        handler.Respond(IndexUrl, IndexJson);
        handler.Respond(NupkgUrl("1.2.0"),
            BuildFakeNupkg(($"{Tfm}", "ALCops.Fake.dll")));

        using var p = Create(AlcopsAnalyzersOption.Latest, handler);
        var run = p.ProvisionAsync(CancellationToken.None);

        Assert.Equal(dir110, await p.Ready.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(p.BackgroundRefresh.IsCompleted);

        handler.Gate.SetResult();
        await p.BackgroundRefresh;

        Assert.True(AlcopsAnalyzerProvisioner.IsCacheValid(Path.Combine(_cacheRoot, Tfm, "1.2.0")));
        Assert.Equal(dir110, await p.Ready);
        await run;
    }

    [Fact]
    public async Task Provision_WarmCache_Latest_UsesNewestCachedStable_NotPrerelease()
    {
        SeedCache("1.2.0");
        SeedCache("1.3.0-preview.1");

        var handler = new FakeHandler { ThrowOnRequest = true };
        var logger = new CapturingLogger();
        using var p = Create(AlcopsAnalyzersOption.Latest, handler, logger);
        await p.ProvisionAsync(CancellationToken.None);
        var result = await p.Ready;

        Assert.NotNull(result);
        Assert.EndsWith("1.2.0", Path.GetFileName(result));

        var info = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("from cache"));
        Assert.Contains("v1.2.0", info.Message);
        Assert.DoesNotContain("SemanticVersion", info.Message);
    }

    [Fact]
    public async Task Provision_WarmCache_Prerelease_UsesNewestCachedAny()
    {
        SeedCache("1.2.0");
        SeedCache("1.3.0-preview.1");

        var handler = new FakeHandler { ThrowOnRequest = true };
        using var p = Create(AlcopsAnalyzersOption.Prerelease, handler);
        await p.ProvisionAsync(CancellationToken.None);
        var result = await p.Ready;

        Assert.NotNull(result);
        Assert.EndsWith("1.3.0-preview.1", Path.GetFileName(result));
    }

    [Fact]
    public async Task Provision_WarmCache_RefreshFailure_NeverThrows()
    {
        var dir110 = SeedCache("1.1.0");

        var handler = new FakeHandler { ThrowOnRequest = true };
        using var p = Create(AlcopsAnalyzersOption.Latest, handler);
        await p.ProvisionAsync(CancellationToken.None);

        Assert.Equal(dir110, await p.Ready);
        Assert.Contains(IndexUrl, handler.RequestUrls);
        Assert.True(p.BackgroundRefresh.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Provision_WarmCache_ShutdownCancelsRefresh()
    {
        var dir110 = SeedCache("1.1.0");

        var handler = new FakeHandler { Gate = new TaskCompletionSource() };
        handler.Respond(IndexUrl, IndexJson);
        handler.Respond(NupkgUrl("1.2.0"),
            BuildFakeNupkg(($"{Tfm}", "ALCops.Fake.dll")));

        using var cts = new CancellationTokenSource();
        using var p = Create(AlcopsAnalyzersOption.Latest, handler);
        var run = p.ProvisionAsync(cts.Token);

        await p.Ready;
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(Directory.Exists(Path.Combine(_cacheRoot, Tfm, "1.2.0")));
    }

    [Fact]
    public async Task Provision_Pinned_Cached_ZeroRequests()
    {
        SeedCache("1.1.0");

        var handler = new FakeHandler();
        using var p = Create(AlcopsAnalyzersOption.Parse("1.1.0"), handler);
        await p.ProvisionAsync(CancellationToken.None);
        var result = await p.Ready;

        Assert.NotNull(result);
        Assert.Empty(handler.RequestUrls);
    }

    [Fact]
    public async Task Fallback_Latest_OnlyPrereleaseCached_UsesItWithExplicitWarning()
    {
        var dir = SeedCache("1.3.0-preview.1");

        var handler = new FakeHandler { ThrowOnRequest = true };
        var logger = new CapturingLogger();
        using var provisioner = Create(AlcopsAnalyzersOption.Latest, handler, logger);
        await provisioner.ProvisionAsync(CancellationToken.None);
        var result = await provisioner.Ready;

        Assert.NotNull(result);
        Assert.Equal(dir, result);
        var warning = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("no stable version cached"));
        Assert.Contains("v1.3.0-preview.1", warning.Message);
        Assert.DoesNotContain("SemanticVersion", warning.Message);
    }

    // The Latest-mode fast path finds the newest stable version in cache and returns
    // it immediately — the prerelease is excluded by includePrerelease: false. No NuGet
    // request is needed for the fast path; no "last resort" warning is logged.
    [Fact]
    public async Task Provision_Latest_StableAndPrereleaseCached_FastPathUsesStable_NoLastResortWarning()
    {
        var stableDir = SeedCache("1.2.0");
        SeedCache("1.3.0-preview.1");

        var handler = new FakeHandler { Gate = new TaskCompletionSource() };
        handler.Respond(IndexUrl, IndexJson);
        var logger = new CapturingLogger();
        using var provisioner = Create(AlcopsAnalyzersOption.Latest, handler, logger);
        var run = provisioner.ProvisionAsync(CancellationToken.None);
        var result = await provisioner.Ready;

        Assert.NotNull(result);
        Assert.Equal(stableDir, result);
        Assert.Empty(handler.RequestUrls);
        Assert.DoesNotContain(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("no stable version cached"));

        handler.Gate.SetResult();
        await run;
    }

    // Documented last-resort behaviour: when the requested pinned version is unavailable
    // and the nupkg download fails, FallbackToCacheOrWarn picks the highest cached
    // version with includePrerelease: true. SemVer 2 orders 1.3.0-preview.1 > 1.2.0
    // (higher base version wins; stability only breaks ties at equal base). This is not
    // a preference for prereleases — it is the most capable version available offline.
    [Fact]
    public async Task Fallback_Prerelease_PicksHighestCachedAcrossStableAndPrerelease()
    {
        SeedCache("1.2.0");
        SeedCache("1.3.0-preview.1");

        var handler = new FakeHandler { ThrowOnRequest = true };
        var logger = new CapturingLogger();
        using var provisioner = Create(AlcopsAnalyzersOption.Parse("9.9.9"), handler, logger);
        await provisioner.ProvisionAsync(CancellationToken.None);
        var result = await provisioner.Ready;

        Assert.NotNull(result);
        Assert.EndsWith("1.3.0-preview.1", Path.GetFileName(result));
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("using cached version"));
    }

    private sealed class CapturingLogger : ILogger<AlcopsAnalyzerProvisioner>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class DirectoryLock : IDisposable
    {
        private readonly Action _cleanup;

        private DirectoryLock(Action cleanup) => _cleanup = cleanup;

        public static DirectoryLock Create(string directory)
        {
            if (OperatingSystem.IsWindows())
            {
                var lockFile = Path.Combine(directory, "lock.bin");
                File.WriteAllBytes(lockFile, [0x00]);
                var stream = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.None);
                return new DirectoryLock(() => stream.Dispose());
            }
            else
            {
#pragma warning disable CA1416 // guarded by OperatingSystem.IsWindows() above; the analyzer loses the guard through the lambda
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                return new DirectoryLock(() =>
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
#pragma warning restore CA1416
            }
        }

        public void Dispose() => _cleanup();
    }

    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.OrdinalIgnoreCase);
        public List<string> RequestUrls { get; } = [];
        public bool ThrowOnRequest { get; set; }
        public Dictionary<string, TimeSpan> Delays { get; } = new(StringComparer.OrdinalIgnoreCase);
        public TaskCompletionSource? Gate { get; set; }

        public void Respond(string url, string json)
        {
            _responses[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }

        public void Respond(string url, byte[] content)
        {
            _responses[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            cancellationToken.ThrowIfCancellationRequested();

            if (ThrowOnRequest)
            {
                RequestUrls.Add(url);
                throw new HttpRequestException("Network unavailable (test)");
            }

            if (Gate is not null)
                await Gate.Task.WaitAsync(cancellationToken);

            RequestUrls.Add(url);

            if (Delays.TryGetValue(url, out var delay))
                await Task.Delay(delay, cancellationToken);

            if (_responses.TryGetValue(url, out var factory))
                return factory();

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
