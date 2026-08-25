using OpcBridge.Hmi.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class HmiClientConfigTests : IDisposable
{
    private readonly string dir_;

    public HmiClientConfigTests()
    {
        dir_ = Path.Combine(Path.GetTempPath(), "OpcBridge.HmiClientConfigTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir_);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(dir_))
            {
                Directory.Delete(dir_, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void CreateDefaultSingleBridge_UsesDefaultId()
    {
        HmiClientConfig config = HmiClientConfig.CreateDefaultSingleBridge("http://192.168.1.10:8080/");
        Assert.Equal("http://192.168.1.10:8080", config.DisplayStoreUrl);
        Assert.Single(config.Bridges);
        Assert.Equal("default", config.Bridges[0].Id);
        Assert.Equal("http://192.168.1.10:8080", config.Bridges[0].BaseUrl);
        Assert.True(config.Bridges[0].Enabled);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        string path = Path.Combine(dir_, "hmi-config.json");
        var config = new HmiClientConfig
        {
            DisplayStoreUrl = "http://primary:8080",
            StartupDisplayId = "plant-overview",
            Bridges =
            [
                new HmiBridgeEndpoint { Id = "line1", BaseUrl = "http://primary:8080/", Enabled = true },
                new HmiBridgeEndpoint { Id = "line2", BaseUrl = "http://peer:8080", Enabled = false }
            ]
        };
        config.Save(path);

        HmiClientConfig loaded = HmiClientConfig.LoadOrDefault(path);
        Assert.Equal("http://primary:8080", loaded.DisplayStoreUrl);
        Assert.Equal("plant-overview", loaded.StartupDisplayId);
        Assert.Equal(2, loaded.Bridges.Count);
        Assert.Equal("line1", loaded.Bridges[0].Id);
        Assert.False(loaded.Bridges[1].Enabled);
        Assert.Single(loaded.EnabledBridges());
    }

    [Fact]
    public void LoadOrDefault_MissingFile_ReturnsSingleBridge()
    {
        string path = Path.Combine(dir_, "missing.json");
        HmiClientConfig loaded = HmiClientConfig.LoadOrDefault(path, "http://127.0.0.1:8080");
        Assert.Single(loaded.Bridges);
        Assert.Equal("default", loaded.Bridges[0].Id);
    }

    [Fact]
    public void TryGetBridge_IsCaseInsensitive()
    {
        HmiClientConfig config = HmiClientConfig.CreateDefaultSingleBridge();
        Assert.True(config.TryGetBridge("DEFAULT", out HmiBridgeEndpoint? ep));
        Assert.Equal("default", ep!.Id);
    }
}
