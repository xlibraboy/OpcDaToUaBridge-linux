using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// API boundary for the per-tag TrendStyle field (HMI trend rendering: Continuous or
/// Step). The dashboard Maps faceplate sends camelCase "trendStyle" through add/update;
/// it must round-trip intact and default to "Continuous" when omitted.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class MappingTrendStyleApiTests
{
    private static void WriteMinimalAppsettings(string dir)
    {
        var appsettings = new
        {
            Da = new { ProgId = "Matrikon.OPC.Simulation.1", Host = "localhost", UpdateRateMs = 1000, UseSubscriptions = true },
            Ua = new
            {
                ApplicationName = "OpcBridge",
                EndpointUrl = "opc.tcp://0.0.0.0:4840/OpcBridge",
                AutoAcceptUntrustedCertificates = true,
                RequireAuthentication = false,
                Username = "",
                Password = "",
                AllowedIpAddresses = Array.Empty<string>()
            },
            Bridge = new { RateLimits = new { }, ExpectedTagCount = 100, Mappings = Array.Empty<object>() },
            Mqtt = new
            {
                Enabled = false,
                BrokerUrl = "tcp://localhost:1883",
                ClientId = "OpcBridge",
                UserName = (string?)null,
                Password = (string?)null,
                Tls = false,
                IgnoreCertErrors = false,
                TopicPrefix = "bridge/tags",
                PayloadFields = "Value, Timestamp"
            }
        };
        File.WriteAllText(
            Path.Combine(dir, "appsettings.json"),
            JsonSerializer.Serialize(appsettings, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(dir, "mappings.json"), "[]");
        string sourcesPath = Path.Combine(dir, "sources.json");
        if (File.Exists(sourcesPath))
        {
            File.Delete(sourcesPath);
        }
    }

    private static StringContent JsonBody(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    private static object TagPayload(string trendStyle) => new
    {
        sourceId = "default",
        itemId = "StyleTag",
        displayName = "Style Tag",
        dataType = "Auto",
        mode = "Source",
        accessRights = "Read",
        enabled = true,
        trendStyle
    };

    private static async Task<string?> GetStoredTrendStyleAsync(TestAppHandle handle)
    {
        using HttpResponseMessage res = await handle.Client.GetAsync("/api/mappings");
        res.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (JsonElement m in doc.RootElement.GetProperty("mappings").EnumerateArray())
        {
            if (m.GetProperty("itemId").GetString() == "StyleTag")
            {
                return m.GetProperty("trendStyle").GetString();
            }
        }

        return null;
    }

    [Fact]
    public async Task Add_WithStepStyle_RoundTrips()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload("Step") } })))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        Assert.Equal("Step", await GetStoredTrendStyleAsync(handle));
    }

    [Fact]
    public async Task Add_OmitsTrendStyle_DefaultsToContinuous()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload(null!) } })))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        Assert.Equal("Continuous", await GetStoredTrendStyleAsync(handle));
    }

    [Fact]
    public async Task Update_ChangesTrendStyle()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage add = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload("Continuous") } })))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        }

        using (HttpResponseMessage upd = await handle.Client.PostAsync(
            "/api/mappings/update", JsonBody(new { tag = TagPayload("Step") })))
        {
            Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        }

        Assert.Equal("Step", await GetStoredTrendStyleAsync(handle));
    }
}
