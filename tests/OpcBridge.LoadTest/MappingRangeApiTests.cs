using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// API boundary for the per-tag engineering range (RangeMin/RangeMax) — the Y axis an HMI
/// trend opens on. The dashboard Maps faceplate sends camelCase "rangeMin"/"rangeMax"
/// through add/update; a complete, ordered pair must round-trip and reach the HMI tag
/// snapshot, while a half-typed or inverted pair is dropped rather than pinning an axis.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class MappingRangeApiTests
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

    private static object TagPayload(double? rangeMin, double? rangeMax) => new
    {
        sourceId = "default",
        itemId = "RangeTag",
        displayName = "Range Tag",
        dataType = "Double",
        mode = "Source",
        accessRights = "Read",
        enabled = true,
        rangeMin,
        rangeMax
    };

    /// <summary>The stored tag as the API reports it, cloned out of the disposed document.</summary>
    private static async Task<JsonElement> GetTagAsync(TestAppHandle handle, string path, string listName)
    {
        using HttpResponseMessage res = await handle.Client.GetAsync(path);
        res.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (JsonElement item in doc.RootElement.GetProperty(listName).EnumerateArray())
        {
            if (item.GetProperty("itemId").GetString() == "RangeTag")
            {
                return item.Clone();
            }
        }

        throw new InvalidOperationException($"RangeTag missing from {path}.");
    }

    private static double? ReadRangeEnd(JsonElement tag, string name)
    {
        return tag.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }

    [Fact]
    public async Task Add_WithRange_RoundTrips()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload(0, 10) } })))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        JsonElement tag = await GetTagAsync(handle, "/api/mappings", "mappings");
        Assert.Equal(0d, ReadRangeEnd(tag, "rangeMin"));
        Assert.Equal(10d, ReadRangeEnd(tag, "rangeMax"));
    }

    [Fact]
    public async Task Add_OmitsRange_LeavesBothEndsUnset()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload(null, null) } })))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        JsonElement tag = await GetTagAsync(handle, "/api/mappings", "mappings");
        Assert.Null(ReadRangeEnd(tag, "rangeMin"));
        Assert.Null(ReadRangeEnd(tag, "rangeMax"));
    }

    [Fact]
    public async Task Add_HalfTypedRange_IsDropped()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload(0, null) } })))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        JsonElement tag = await GetTagAsync(handle, "/api/mappings", "mappings");
        Assert.Null(ReadRangeEnd(tag, "rangeMin"));
        Assert.Null(ReadRangeEnd(tag, "rangeMax"));
    }

    [Fact]
    public async Task Add_InvertedRange_IsDropped()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload(10, 0) } })))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        JsonElement tag = await GetTagAsync(handle, "/api/mappings", "mappings");
        Assert.Null(ReadRangeEnd(tag, "rangeMin"));
        Assert.Null(ReadRangeEnd(tag, "rangeMax"));
    }

    [Fact]
    public async Task Update_SetsAndClearsRange()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage add = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload(null, null) } })))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        }

        using (HttpResponseMessage set = await handle.Client.PostAsync(
            "/api/mappings/update", JsonBody(new { tag = TagPayload(-5.5, 100.25) })))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        JsonElement stored = await GetTagAsync(handle, "/api/mappings", "mappings");
        Assert.Equal(-5.5d, ReadRangeEnd(stored, "rangeMin"));
        Assert.Equal(100.25d, ReadRangeEnd(stored, "rangeMax"));

        using (HttpResponseMessage cleared = await handle.Client.PostAsync(
            "/api/mappings/update", JsonBody(new { tag = TagPayload(null, null) })))
        {
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        }

        JsonElement after = await GetTagAsync(handle, "/api/mappings", "mappings");
        Assert.Null(ReadRangeEnd(after, "rangeMin"));
        Assert.Null(ReadRangeEnd(after, "rangeMax"));
    }

    [Fact]
    public async Task HmiTagSnapshot_CarriesRange()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using (HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/add", JsonBody(new { tags = new[] { TagPayload(0, 10) } })))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        // The HMI reads tag metadata from /api/hmi/tags, so the range has to travel with it.
        JsonElement tag = await GetTagAsync(handle, "/api/hmi/tags", "tags");
        Assert.Equal(0d, ReadRangeEnd(tag, "rangeMin"));
        Assert.Equal(10d, ReadRangeEnd(tag, "rangeMax"));
    }
}
