using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// API boundary for the per-tag Decimals setting: what the dashboard sends
/// (camelCase "decimals") must round-trip through add/update and come back
/// intact. Null stays null (no rounding).
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class MappingDecimalsApiTests
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

    private static object TagPayload(int? decimals) => new
    {
        sourceId = "default",
        itemId = "DemoTag",
        displayName = "Demo Tag",
        dataType = "Double",
        mode = "Manual",
        manualValue = "3.14159265",
        decimals,
        accessRights = "Read",
        enabled = true
    };

    private static async Task<int?> GetStoredDecimalsAsync(TestAppHandle handle)
    {
        using HttpResponseMessage res = await handle.Client.GetAsync("/api/mappings");
        res.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (JsonElement m in doc.RootElement.GetProperty("mappings").EnumerateArray())
        {
            if (m.GetProperty("itemId").GetString() == "DemoTag")
            {
                return m.GetProperty("decimals").ValueKind == JsonValueKind.Null
                    ? null
                    : m.GetProperty("decimals").GetInt32();
            }
        }

        return null;
    }

    [Fact]
    public async Task Add_MappingWithDecimals_RoundTrips()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload(2) } })))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        Assert.Equal(2, await GetStoredDecimalsAsync(handle));
    }

    [Fact]
    public async Task Update_MappingWithDecimals_RoundTrips()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage add = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload(null) } })))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        }

        using (HttpResponseMessage upd = await handle.Client.PostAsync(
            "/api/mappings/update", JsonBody(new { tag = TagPayload(3) })))
        {
            Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        }

        Assert.Equal(3, await GetStoredDecimalsAsync(handle));
    }

    [Fact]
    public async Task Update_MappingDecimalsCleared_StaysNull()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage add = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload(2) } })))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        }

        using (HttpResponseMessage upd = await handle.Client.PostAsync(
            "/api/mappings/update", JsonBody(new { tag = TagPayload(null) })))
        {
            Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        }

        Assert.Null(await GetStoredDecimalsAsync(handle));
    }
}
