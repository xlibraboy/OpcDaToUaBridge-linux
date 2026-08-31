using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;
using OpcBridge.Hmi.ViewModels;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>Bridge rows: local/external classification, store fallback, config mapping.</summary>
public sealed class BridgeRowTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080", "Local")]
    [InlineData("http://localhost:8080", "Local")]
    [InlineData("http://LOCALHOST:8080", "Local")]
    [InlineData("http://192.168.1.11:8080", "External")]
    [InlineData("http://DESKTOP-BC2AU7H:8080", "External")]
    [InlineData("not a url", "Local")]
    [InlineData("", "Local")]
    public void ScopeKind_ClassifiesByHost(string address, string expected)
    {
        var row = new BridgeRow { Address = address };
        Assert.Equal(expected, row.ScopeKind);
    }

    [Fact]
    public void StoreUrl_FallsBackToAddress()
    {
        var row = new BridgeRow { Address = "http://192.168.1.11:8080/", DisplayStore = "" };
        Assert.Equal("http://192.168.1.11:8080", row.StoreUrl);

        row.DisplayStore = "http://192.168.1.11:9090";
        Assert.Equal("http://192.168.1.11:9090", row.StoreUrl);
    }

    [Fact]
    public void BuildConfig_SkipsEmptyRows_AndNamesBridges()
    {
        var config = MainViewModel.BuildConfigFromRows(
        [
            new BridgeRow { Address = "http://127.0.0.1:8080" },
            new BridgeRow { Address = "", Name = "ignored" },
            new BridgeRow { Name = "line2", Address = "http://192.168.1.11:8080" }
        ]);

        Assert.Equal(2, config.Bridges.Count);
        Assert.Equal("default", config.Bridges[0].Id);
        Assert.Equal("line2", config.Bridges[1].Id);
        Assert.Equal("http://127.0.0.1:8080", config.DisplayStoreUrl);
    }

    [Fact]
    public void BuildConfig_WritesPerRowStore_OnlyWhenOverridden()
    {
        var config = MainViewModel.BuildConfigFromRows(
        [
            new BridgeRow { Address = "http://127.0.0.1:8080" },
            new BridgeRow { Name = "line2", Address = "http://192.168.1.11:8080", DisplayStore = "http://192.168.1.11:9090" }
        ]);

        Assert.Equal(string.Empty, config.Bridges[0].DisplayStoreUrl);
        Assert.Equal("http://192.168.1.11:9090", config.Bridges[1].DisplayStoreUrl);
        Assert.Equal("http://127.0.0.1:8080", config.DisplayStoreUrl);
    }

    [Fact]
    public void BuildConfig_DeduplicatesIds()
    {
        var config = MainViewModel.BuildConfigFromRows(
        [
            new BridgeRow { Name = "line2", Address = "http://a:1" },
            new BridgeRow { Name = "line2", Address = "http://b:1" }
        ]);

        Assert.Equal("line2", config.Bridges[0].Id);
        Assert.Equal("line2-2", config.Bridges[1].Id);
    }

    [Fact]
    public void ConfigRoundTrip_PreservesPerBridgeStore()
    {
        string path = Path.Combine(Path.GetTempPath(), "hmi-config-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var original = new HmiClientConfig
            {
                DisplayStoreUrl = "http://127.0.0.1:8080",
                Bridges =
                [
                    new HmiBridgeEndpoint { Id = "default", BaseUrl = "http://127.0.0.1:8080", DisplayStoreUrl = "" },
                    new HmiBridgeEndpoint { Id = "line2", BaseUrl = "http://192.168.1.11:8080", DisplayStoreUrl = "http://192.168.1.11:9090" }
                ]
            };
            original.Save(path);

            HmiClientConfig loaded = HmiClientConfig.LoadOrDefault(path);
            Assert.Equal("http://192.168.1.11:9090", loaded.Bridges[1].DisplayStoreUrl);
            Assert.Equal(string.Empty, loaded.Bridges[0].DisplayStoreUrl);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Detector_ReturnsNull_WhenNothingListens()
    {
        string? found = await LocalBridgeDetector.DetectAsync(["http://127.0.0.1:59999"], timeoutMs: 300);
        Assert.Null(found);
    }

    [Fact]
    public async Task Detector_FindsLocalListener()
    {
        // Minimal HTTP listener standing in for a local OpcBridge.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        Task serve = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using System.Net.Sockets.TcpClient client = await listener.AcceptTcpClientAsync();
                    byte[] response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n{}"u8.ToArray();
                    await client.GetStream().WriteAsync(response);
                }
            }
            catch
            {
                // listener stopped
            }
        });

        try
        {
            string? found = await LocalBridgeDetector.DetectAsync([$"http://127.0.0.1:{port}"], timeoutMs: 1500);
            Assert.Equal($"http://127.0.0.1:{port}", found);
        }
        finally
        {
            listener.Stop();
        }
    }
}
