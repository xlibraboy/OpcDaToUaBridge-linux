using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// API boundary for the per-tag Digital / OnText / OffText fields (HMI faceplate on/off
/// status text). The dashboard faceplate sends camelCase payloads through add/update;
/// Digital must stay tri-state (null = auto, true = digital, false = analog) and survive
/// a round-trip, while omitted values default to auto with no labels.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class MappingDigitalApiTests
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

    private static object TagPayload(string dataType, bool? digital, string? onText, string? offText) => new
    {
        sourceId = "default",
        itemId = "DigitalTag",
        displayName = "Digital Tag",
        dataType,
        mode = "Source",
        accessRights = "Read",
        enabled = true,
        digital,
        onText,
        offText
    };

    private static async Task<JsonElement> GetStoredTagAsync(TestAppHandle handle)
    {
        using HttpResponseMessage res = await handle.Client.GetAsync("/api/mappings");
        res.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (JsonElement m in doc.RootElement.GetProperty("mappings").EnumerateArray())
        {
            if (m.GetProperty("itemId").GetString() == "DigitalTag")
            {
                return m.Clone();
            }
        }

        throw new InvalidOperationException("DigitalTag not found.");
    }

    [Fact]
    public async Task Add_ByteTagMarkedDigital_RoundTripsWithLabels()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload("Byte", true, "Running", "Stopped") } })))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        JsonElement stored = await GetStoredTagAsync(handle);
        Assert.True(stored.GetProperty("digital").GetBoolean());
        Assert.Equal("Running", stored.GetProperty("onText").GetString());
        Assert.Equal("Stopped", stored.GetProperty("offText").GetString());
    }

    [Fact]
    public async Task Add_BooleanTagOmittingDigital_StaysAuto()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload("Boolean", null, null, null) } })))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        JsonElement stored = await GetStoredTagAsync(handle);
        // Tri-state must survive: null means "auto", not "analog".
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("digital").ValueKind);
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("onText").ValueKind);
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("offText").ValueKind);
    }

    [Fact]
    public async Task Add_BooleanTagExplicitlyAnalog_RoundTripsFalse()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload("Boolean", false, null, null) } })))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        JsonElement stored = await GetStoredTagAsync(handle);
        Assert.False(stored.GetProperty("digital").GetBoolean());
    }

    [Fact]
    public async Task Update_ChangesDigitalAndLabels()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage add = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload("Byte", null, null, null) } })))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        }

        using (HttpResponseMessage upd = await handle.Client.PostAsync(
            "/api/mappings/update", JsonBody(new { tag = TagPayload("Byte", true, "Open", "Closed") })))
        {
            Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        }

        JsonElement stored = await GetStoredTagAsync(handle);
        Assert.True(stored.GetProperty("digital").GetBoolean());
        Assert.Equal("Open", stored.GetProperty("onText").GetString());
        Assert.Equal("Closed", stored.GetProperty("offText").GetString());
    }

    [Fact]
    public async Task Update_BlankLabels_NormalizeToNull()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage add = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload("Byte", true, "On", "Off") } })))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        }

        using (HttpResponseMessage upd = await handle.Client.PostAsync(
            "/api/mappings/update", JsonBody(new { tag = TagPayload("Byte", true, "   ", "") })))
        {
            Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        }

        JsonElement stored = await GetStoredTagAsync(handle);
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("onText").ValueKind);
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("offText").ValueKind);
    }
}
