using System.IO.Compression;
using System.Net;
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

    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.OrdinalIgnoreCase);
        public List<string> RequestUrls { get; } = [];
        public bool ThrowOnRequest { get; set; }

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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestUrls.Add(url);

            if (ThrowOnRequest)
                throw new HttpRequestException("Network unavailable (test)");

            if (_responses.TryGetValue(url, out var factory))
                return Task.FromResult(factory());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
