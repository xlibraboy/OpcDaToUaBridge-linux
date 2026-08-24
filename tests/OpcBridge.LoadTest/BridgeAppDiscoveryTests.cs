 using System.Net;
 using System.Text.Json;
 using Microsoft.Extensions.Logging;
 using Microsoft.Extensions.Logging.Abstractions;
 using Microsoft.Extensions.Options;
 using OpcBridge.App;
 using OpcBridge.Da;
 using Xunit;

namespace OpcBridge.LoadTest;

// Joins InterlinkApiAppCollection because UpsertSource persists to (and the ctor deletes)
// AppContext.BaseDirectory/sources.json — the same shared file UaSourceSubscriptionsTests
// round-trips; running as a parallel implicit collection raced its disk assertions.
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class BridgeAppDiscoveryTests : IDisposable
{
    private readonly string _sourcesJsonPath;

    public BridgeAppDiscoveryTests()
    {
        _sourcesJsonPath = Path.Combine(AppContext.BaseDirectory, "sources.json");
        if (File.Exists(_sourcesJsonPath))
        {
            File.Delete(_sourcesJsonPath);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_sourcesJsonPath))
        {
            File.Delete(_sourcesJsonPath);
        }
    }

    [Fact]
    public async Task LocalHostAliases_DoNotIncreaseCount()
    {
        var options = Options.Create(new DaClientOptions());
        var settings = new DaRuntimeSettings(options);

        // Add sources with local aliases
        settings.UpsertSource(DaRuntimeSettings.CreateDaSource("s1", "S1", "ProgId", "localhost", null, null, null, 1000));
        settings.UpsertSource(DaRuntimeSettings.CreateDaSource("s2", "S2", "ProgId", ".", null, null, null, 1000));
        settings.UpsertSource(DaRuntimeSettings.CreateDaSource("s3", "S3", "ProgId", "127.0.0.1", null, null, null, 1000));
        settings.UpsertSource(DaRuntimeSettings.CreateDaSource("s4", "S4", "ProgId", Environment.MachineName, null, null, null, 1000));

        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var factory = new StubHttpClientFactory(httpClient);
        var logger = NullLogger<BridgeAppDiscovery>.Instance;

        var discovery = new BridgeAppDiscovery(settings, factory, logger);

        await discovery.RefreshAsync(CancellationToken.None);

        var status = discovery.GetStatus();
        Assert.Equal(1, status.DetectedCount);
        Assert.Single(status.DetectedApps);
        Assert.True(status.DetectedApps[0].IsLocal);
    }

    [Fact]
    public async Task OneHealthyRemoteHost_IsCounted()
    {
        var options = Options.Create(new DaClientOptions());
        var settings = new DaRuntimeSettings(options);
        settings.UpsertSource(DaRuntimeSettings.CreateDaSource("remote", "Remote", "ProgId", "REMOTE-HOST", null, null, null, 1000));

        var handler = new StubHttpMessageHandler();
        handler.AddResponse("http://REMOTE-HOST:8080/health", new { status = "ok" });
        handler.AddResponse("http://REMOTE-HOST:8080/api/app-info", new
        {
            name = "OpcBridge.App",
            version = "1.2.3.4",
            informationalVersion = "1.2.3",
            framework = ".NET 8.0",
            processArchitecture = "X64",
            osDescription = "Windows",
            machineName = "REMOTE-HOST",
            creator = "xlibraboy"
        });

        var httpClient = new HttpClient(handler);
        var factory = new StubHttpClientFactory(httpClient);
        var logger = NullLogger<BridgeAppDiscovery>.Instance;

        var discovery = new BridgeAppDiscovery(settings, factory, logger);

        await discovery.RefreshAsync(CancellationToken.None);

        var status = discovery.GetStatus();
        Assert.Equal(2, status.DetectedCount);
        Assert.Equal(2, status.DetectedApps.Count);

        var local = status.DetectedApps.FirstOrDefault(a => a.IsLocal);
        Assert.NotNull(local);

        var remote = status.DetectedApps.FirstOrDefault(a => !a.IsLocal);
        Assert.NotNull(remote);
        Assert.Equal("REMOTE-HOST", remote.MachineName);
        Assert.Equal("REMOTE-HOST", remote.ProbeHost);
        Assert.Equal("1.2.3.4", remote.Version);
    }

    [Fact]
    public async Task FailedRemoteHost_IsIgnored()
    {
        var options = Options.Create(new DaClientOptions());
        var settings = new DaRuntimeSettings(options);
        settings.UpsertSource(DaRuntimeSettings.CreateDaSource("healthy", "Healthy", "ProgId", "HEALTHY-HOST", null, null, null, 1000));
        settings.UpsertSource(DaRuntimeSettings.CreateDaSource("failing", "Failing", "ProgId", "FAILING-HOST", null, null, null, 1000));

        var handler = new StubHttpMessageHandler();
        handler.AddResponse("http://HEALTHY-HOST:8080/health", new { status = "ok" });
        handler.AddResponse("http://HEALTHY-HOST:8080/api/app-info", new
        {
            name = "OpcBridge.App",
            version = "1.0.0.0",
            informationalVersion = "1.0.0",
            framework = ".NET 8.0",
            processArchitecture = "X64",
            osDescription = "Windows",
            machineName = "HEALTHY-HOST",
            creator = "xlibraboy"
        });
        handler.AddFailure("http://FAILING-HOST:8080/health", new HttpRequestException("Connection refused"));

        var httpClient = new HttpClient(handler);
        var factory = new StubHttpClientFactory(httpClient);
        var logger = NullLogger<BridgeAppDiscovery>.Instance;

        var discovery = new BridgeAppDiscovery(settings, factory, logger);

        await discovery.RefreshAsync(CancellationToken.None);

        var status = discovery.GetStatus();
        Assert.Equal(2, status.DetectedCount); // local + healthy remote
        Assert.Equal(2, status.DetectedApps.Count);

        var remote = status.DetectedApps.FirstOrDefault(a => !a.IsLocal);
        Assert.NotNull(remote);
        Assert.Equal("HEALTHY-HOST", remote.MachineName);
    }

    [Fact]
    public async Task DuplicateAliasesForSameMachine_CollapseByMachineName()
    {
        var options = Options.Create(new DaClientOptions());
        var settings = new DaRuntimeSettings(options);
        settings.UpsertSource(DaRuntimeSettings.CreateDaSource("alias1", "Alias1", "ProgId", "HOST-A", null, null, null, 1000));
        settings.UpsertSource(DaRuntimeSettings.CreateDaSource("alias2", "Alias2", "ProgId", "192.168.1.100", null, null, null, 1000));

        var handler = new StubHttpMessageHandler();
        // Both aliases return the same machineName
        handler.AddResponse("http://HOST-A:8080/health", new { status = "ok" });
        handler.AddResponse("http://HOST-A:8080/api/app-info", new
        {
            name = "OpcBridge.App",
            version = "1.0.0.0",
            informationalVersion = "1.0.0",
            framework = ".NET 8.0",
            processArchitecture = "X64",
            osDescription = "Windows",
            machineName = "ACTUAL-MACHINE",
            creator = "xlibraboy"
        });
        handler.AddResponse("http://192.168.1.100:8080/health", new { status = "ok" });
        handler.AddResponse("http://192.168.1.100:8080/api/app-info", new
        {
            name = "OpcBridge.App",
            version = "1.0.0.0",
            informationalVersion = "1.0.0",
            framework = ".NET 8.0",
            processArchitecture = "X64",
            osDescription = "Windows",
            machineName = "ACTUAL-MACHINE",
            creator = "xlibraboy"
        });

        var httpClient = new HttpClient(handler);
        var factory = new StubHttpClientFactory(httpClient);
        var logger = NullLogger<BridgeAppDiscovery>.Instance;

        var discovery = new BridgeAppDiscovery(settings, factory, logger);

        await discovery.RefreshAsync(CancellationToken.None);

        var status = discovery.GetStatus();
        Assert.Equal(2, status.DetectedCount); // local + 1 remote (deduplicated)
        Assert.Equal(2, status.DetectedApps.Count);

        var remote = status.DetectedApps.FirstOrDefault(a => !a.IsLocal);
        Assert.NotNull(remote);
        Assert.Equal("ACTUAL-MACHINE", remote.MachineName);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Exception> _failures = new(StringComparer.OrdinalIgnoreCase);

        public void AddResponse<T>(string url, T body)
        {
            var json = JsonSerializer.Serialize(body);
            _responses[url] = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }

        public void AddFailure(string url, Exception exception)
        {
            _failures[url] = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            if (_failures.TryGetValue(url, out var failure))
            {
                throw failure;
            }

            if (_responses.TryGetValue(url, out var response))
            {
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }
}
