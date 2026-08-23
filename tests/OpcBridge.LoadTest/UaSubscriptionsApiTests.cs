using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Wire API for named OPC UA subscriptions: upsert/list/remove under
/// /api/ua/subscriptions and the mapping-level subscription field round-trip
/// through /api/mappings (spec §§4–6).
/// </summary>
[Collection(nameof(DaLinkApiAppCollection))]
public sealed class UaSubscriptionsApiTests
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

    /// <summary>Seeds an OpcUa-typed source through the dashboard's own endpoint.</summary>
    private static async Task SeedUaSourceAsync(TestAppHandle handle, string sourceId)
    {
        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId,
                displayName = "UA " + sourceId,
                sourceType = "OpcUa",
                endpointUrl = "opc.tcp://127.0.0.1:49320",
                securityMode = "None",
                securityPolicy = "None",
                updateRateMs = 1000,
                maxMappedTags = 50000,
                useSubscriptions = true
            }));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private static JsonElement GetSource(JsonDocument doc, string sourceId)
    {
        foreach (JsonElement source in doc.RootElement.GetProperty("sources").EnumerateArray())
        {
            if (string.Equals(source.GetProperty("sourceId").GetString(), sourceId, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        throw new Xunit.Sdk.XunitException($"Source '{sourceId}' not found in GET /api/ua/subscriptions response.");
    }

    private static JsonElement GetMapping(JsonElement mappings, string sourceId, string itemId)
    {
        foreach (JsonElement el in mappings.EnumerateArray())
        {
            if (string.Equals(el.GetProperty("sourceId").GetString(), sourceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(el.GetProperty("itemId").GetString(), itemId, StringComparison.OrdinalIgnoreCase))
            {
                return el;
            }
        }

        throw new Xunit.Sdk.XunitException($"Mapping '{sourceId}/{itemId}' not found in GET /api/mappings response.");
    }

    [Fact]
    public async Task Upsert_List_Remove_FullCycle_MovesBoundMappingBackToDefault()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedUaSourceAsync(handle, "ua-t");

        // 1. Upsert "Fast" @250 ms.
        using (HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/subscriptions",
            JsonBody(new { sourceId = "ua-t", name = "Fast", updateRateMs = 250 })))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(body.RootElement.TryGetProperty("version", out _));
        }

        // 2. List shows the definition (live status absent -> zeroed placeholders).
        using (JsonDocument list = await handle.GetJsonAsync("/api/ua/subscriptions?sourceId=ua-t"))
        {
            JsonElement subs = GetSource(list, "ua-t").GetProperty("subscriptions");
            Assert.Equal(1, subs.GetArrayLength());
            JsonElement fast = subs[0];
            Assert.Equal("Fast", fast.GetProperty("name").GetString());
            Assert.Equal(250, fast.GetProperty("updateRateMs").GetInt32());
            Assert.Equal(0, fast.GetProperty("itemCount").GetInt32());
            Assert.False(fast.GetProperty("created").GetBoolean());
        }

        // 3. Bind a mapping to "Fast", then remove the subscription.
        using (HttpResponseMessage add = await handle.Client.PostAsync(
            "/api/mappings/add",
            JsonBody(new
            {
                tags = new[]
                {
                    new { sourceId = "ua-t", itemId = "Channel1.Device1.Tag1", uaNodeId = "ns=2;s=Tag1", subscription = "Fast" }
                }
            })))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        }

        using (HttpResponseMessage remove = await handle.Client.PostAsync(
            "/api/ua/subscriptions/remove",
            JsonBody(new { sourceId = "ua-t", name = "Fast" })))
        {
            Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await remove.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1, body.RootElement.GetProperty("movedMappings").GetInt32());
        }

        // 4. The removed definition is gone and its mapping moved back to default.
        using (JsonDocument list = await handle.GetJsonAsync("/api/ua/subscriptions?sourceId=ua-t"))
        {
            Assert.Equal(0, GetSource(list, "ua-t").GetProperty("subscriptions").GetArrayLength());
        }

        using (JsonDocument mappings = await handle.GetJsonAsync("/api/mappings"))
        {
            JsonElement tag = GetMapping(mappings.RootElement.GetProperty("mappings"), "ua-t", "Channel1.Device1.Tag1");
            Assert.Equal(string.Empty, tag.GetProperty("subscription").GetString());
        }
    }

    [Fact]
    public async Task Upsert_DuplicateNameDifferentCase_UpdatesInPlace()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedUaSourceAsync(handle, "ua-t");

        using (HttpResponseMessage first = await handle.Client.PostAsync(
            "/api/ua/subscriptions",
            JsonBody(new { sourceId = "ua-t", name = "Fast", updateRateMs = 250 })))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (HttpResponseMessage second = await handle.Client.PostAsync(
            "/api/ua/subscriptions",
            JsonBody(new { sourceId = "ua-t", name = "fASt", updateRateMs = 600 })))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        using JsonDocument list = await handle.GetJsonAsync("/api/ua/subscriptions?sourceId=ua-t");
        JsonElement subs = GetSource(list, "ua-t").GetProperty("subscriptions");
        Assert.Equal(1, subs.GetArrayLength());
        Assert.Equal("fASt", subs[0].GetProperty("name").GetString());
        Assert.Equal(600, subs[0].GetProperty("updateRateMs").GetInt32());
    }

    [Fact]
    public async Task Upsert_NonPositiveRate_Returns400()
    {
        // Spec §3: rates <= 0 are rejected at the API layer as an operator error,
        // mirroring /api/da/update-rate; only positive rates below the floor are clamped.
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedUaSourceAsync(handle, "ua-t");

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/subscriptions",
            JsonBody(new { sourceId = "ua-t", name = "TooFast", updateRateMs = -5 }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("error", out JsonElement error));
        Assert.Contains("positive", error.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upsert_BlankName_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedUaSourceAsync(handle, "ua-t");

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/subscriptions",
            JsonBody(new { sourceId = "ua-t", name = "   ", updateRateMs = 250 }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        string body = await res.Content.ReadAsStringAsync();
        Assert.Contains("1-64", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upsert_UnknownSource_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/subscriptions",
            JsonBody(new { sourceId = "ghost", name = "Fast", updateRateMs = 250 }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        string body = await res.Content.ReadAsStringAsync();
        Assert.Contains("does not exist", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remove_MissingSubscription_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedUaSourceAsync(handle, "ua-t");

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/subscriptions/remove",
            JsonBody(new { sourceId = "ua-t", name = "Nope" }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        string body = await res.Content.ReadAsStringAsync();
        Assert.Contains("does not exist", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappingAdd_SubscriptionField_RoundTrips_And_ToleratesUnknownNames()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedUaSourceAsync(handle, "ua-t");

        using (HttpResponseMessage add = await handle.Client.PostAsync(
            "/api/mappings/add",
            JsonBody(new
            {
                tags = new[]
                {
                    new { sourceId = "ua-t", itemId = "Channel1.Device1.T1", uaNodeId = "ns=2;s=T1", subscription = "MysterySub" },
                    new { sourceId = "ua-t", itemId = "Channel1.Device1.T2", uaNodeId = "ns=2;s=T2", subscription = "" }
                }
            })))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        }

        using JsonDocument mappings = await handle.GetJsonAsync("/api/mappings");
        JsonElement all = mappings.RootElement.GetProperty("mappings");
        Assert.Equal("MysterySub", GetMapping(all, "ua-t", "Channel1.Device1.T1").GetProperty("subscription").GetString());
        Assert.Equal(string.Empty, GetMapping(all, "ua-t", "Channel1.Device1.T2").GetProperty("subscription").GetString());
    }
}
