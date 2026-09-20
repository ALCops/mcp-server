using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using ALCops.Mcp;
using ALCops.Mcp.Services;
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

    private string SeedCache(string version, params string[] files)
    {
        if (files.Length == 0)
            files = ["ALCops.Fake.dll"];

        var dir = Path.Combine(_cacheRoot, Tfm, version);
        Directory.CreateDirectory(dir);

        foreach (var file in files)
            File.WriteAllBytes(Path.Combine(dir, file), [0x4D, 0x5A]);

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

        return dir;
    }

    private string SeedInvalidCache(string version)
    {
        var dir = Path.Combine(_cacheRoot, Tfm, version);
        Directory.CreateDirectory(dir);

        var manifest = new
        {
            alcopsVersion = version,
            requestedTfm = Tfm,
            targetFramework = Tfm,
            downloadedAt = DateTime.UtcNow.ToString("o"),
            files = new[] { "ALCops.Fake.dll" },
            source = "test"
        };

        File.WriteAllText(
            Path.Combine(dir, ".alcops-manifest.json"),
            JsonSerializer.Serialize(manifest, JsonDefaults.Options));

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
            RequestUrls.Add(url);

            cancellationToken.ThrowIfCancellationRequested();

            if (ThrowOnRequest)
                throw new HttpRequestException("Network unavailable (test)");

            if (Gate is not null)
                await Gate.Task.WaitAsync(cancellationToken);

            if (Delays.TryGetValue(url, out var delay))
                await Task.Delay(delay, cancellationToken);

            if (_responses.TryGetValue(url, out var factory))
                return factory();

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
