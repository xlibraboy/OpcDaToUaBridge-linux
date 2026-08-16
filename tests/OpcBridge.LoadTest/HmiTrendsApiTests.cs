using System.Net;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(DaLinkApiAppCollection))]
public sealed class HmiTrendsApiTests
{
    private static void WriteAppsettings(string dir)
    {
        var appsettings = new
        {
            Da = new { ProgId = "Matrikon.OPC.Simulation.1", Host = "localhost", UpdateRateMs = 1000, UseSubscriptions = true },
            Ua = new { ApplicationName = "OpcDaToUaBridge", EndpointUrl = "opc.tcp://0.0.0.0:4840/OpcBridge", AutoAcceptUntrustedCertificates = true, RequireAuthentication = false, Username = "", Password = "", AllowedIpAddresses = Array.Empty<string>() },
            Bridge = new { RateLimits = new { }, ExpectedTagCount = 100, Mappings = Array.Empty<object>() },
            Mqtt = new { Enabled = false, BrokerUrl = "tcp://localhost:1883", ClientId = "OpcDaToUaBridge", UserName = (string?)null, Password = (string?)null, Tls = false, IgnoreCertErrors = false, TopicPrefix = "bridge/tags", PayloadFields = "Value, Timestamp" }
        };
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), JsonSerializer.Serialize(appsettings, new JsonSerializerOptions { WriteIndented = true }));
        string mapPath = Path.Combine(dir, "mappings.json");
        if (File.Exists(mapPath)) File.Delete(mapPath);
    }

    [Fact]
    public async Task HmiTrends_MissingSourceId_Returns400()
    {
        await using var handle = await TestAppHandle.StartAsync(WriteAppsettings);
        using HttpResponseMessage response = await handle.Client.GetAsync("/api/hmi/trends?itemId=Random.Int1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HmiTrends_MissingDaItemId_Returns400()
    {
        await using var handle = await TestAppHandle.StartAsync(WriteAppsettings);
        using HttpResponseMessage response = await handle.Client.GetAsync("/api/hmi/trends?sourceId=default");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HmiTrends_StubUnavailable_Returns200EmptyWithError()
    {
        await using var handle = await TestAppHandle.StartAsync(WriteAppsettings);
        using HttpResponseMessage response = await handle.Client.GetAsync(
            "/api/hmi/trends?sourceId=default&itemId=Random.Int1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("default", doc.RootElement.GetProperty("sourceId").GetString());
        Assert.Equal("Random.Int1", doc.RootElement.GetProperty("itemId").GetString());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("points").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("points").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
    }
}
