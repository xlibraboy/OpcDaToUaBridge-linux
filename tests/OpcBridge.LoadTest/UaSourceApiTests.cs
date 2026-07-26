using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(DaLinkApiAppCollection))]
public sealed class UaSourceApiTests
{
    private static void WriteMinimalAppsettings(string dir, string uaEndpoint = "opc.tcp://0.0.0.0:4840/OpcBridge")
    {
        var appsettings = new
        {
            Da = new { ProgId = "Matrikon.OPC.Simulation.1", Host = "localhost", UpdateRateMs = 1000, UseSubscriptions = true },
            Ua = new
            {
                ApplicationName = "OpcDaToUaBridge",
                EndpointUrl = uaEndpoint,
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
                ClientId = "OpcDaToUaBridge",
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

    private static JsonElement FindSource(JsonDocument list, string sourceId)
    {
        foreach (JsonElement source in list.RootElement.GetProperty("sources").EnumerateArray())
        {
            if (string.Equals(source.GetProperty("sourceId").GetString(), sourceId, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        throw new Xunit.Sdk.XunitException($"Source '{sourceId}' not found in GET /api/da/sources response.");
    }

    [Fact]
    public async Task PostSource_OpcUa_PersistsTypeAndEndpoint()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId = "kep",
                displayName = "Kepware",
                sourceType = "OpcUa",
                endpointUrl = "opc.tcp://127.0.0.1:49320",
                securityMode = "None",
                securityPolicy = "None",
                updateRateMs = 1000,
                maxMappedTags = 50000,
                useSubscriptions = true
            }));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        JsonElement source = body.RootElement.GetProperty("source");
        Assert.Equal("OpcUa", source.GetProperty("sourceType").GetString());
        Assert.Equal("opc.tcp://127.0.0.1:49320", source.GetProperty("endpointUrl").GetString());
        Assert.Equal(50000, source.GetProperty("maxMappedTags").GetInt32());
        Assert.True(source.GetProperty("useSubscriptions").GetBoolean());

        using JsonDocument list = await handle.GetJsonAsync("/api/da/sources");
        JsonElement listed = FindSource(list, "kep");
        Assert.Equal("OpcUa", listed.GetProperty("sourceType").GetString());
        Assert.Equal("opc.tcp://127.0.0.1:49320", listed.GetProperty("endpointUrl").GetString());
        Assert.Equal("None", listed.GetProperty("securityMode").GetString());
        Assert.Equal("None", listed.GetProperty("securityPolicy").GetString());
        Assert.Equal(50000, listed.GetProperty("maxMappedTags").GetInt32());
    }

    [Fact]
    public async Task GetSources_IncludesSourceTypeForDefaultDa()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using JsonDocument list = await handle.GetJsonAsync("/api/da/sources");
        JsonElement listed = FindSource(list, "default");
        Assert.Equal("OpcDa", listed.GetProperty("sourceType").GetString());
        Assert.False(string.IsNullOrWhiteSpace(listed.GetProperty("progId").GetString()));
        Assert.True(listed.TryGetProperty("endpointUrl", out _));
        Assert.True(listed.TryGetProperty("maxMappedTags", out _));
    }

    [Fact]
    public async Task PostSource_OpcUa_SelfEndpoint_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir =>
            WriteMinimalAppsettings(dir, uaEndpoint: "opc.tcp://0.0.0.0:4840/OpcBridge"));

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId = "self",
                displayName = "Self",
                sourceType = "OpcUa",
                endpointUrl = "opc.tcp://127.0.0.1:4840/OpcBridge",
                securityMode = "None",
                securityPolicy = "None"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        string? error = body.RootElement.GetProperty("error").GetString();
        Assert.Contains("own OPC UA server", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostSource_OpcUa_MissingEndpoint_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId = "bad",
                displayName = "Bad",
                sourceType = "OpcUa",
                securityMode = "None",
                securityPolicy = "None"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Contains("Endpoint URL", body.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostSource_OpcDa_MissingProgId_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId = "line2",
                displayName = "Line 2",
                sourceType = "OpcDa",
                host = "localhost"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Contains("ProgId", body.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostSource_OpcUa_InvalidSecurityPair_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId = "sec",
                displayName = "Sec",
                sourceType = "OpcUa",
                endpointUrl = "opc.tcp://127.0.0.1:49320",
                securityMode = "Sign",
                securityPolicy = "None"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Contains("Security", body.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostMappingsAdd_OpcUa_ExceedsMaxMappedTags_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage sourceRes = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId = "kep",
                displayName = "Kepware",
                sourceType = "OpcUa",
                endpointUrl = "opc.tcp://127.0.0.1:49320",
                securityMode = "None",
                securityPolicy = "None",
                maxMappedTags = 2,
                updateRateMs = 1000
            }));
        Assert.Equal(HttpStatusCode.OK, sourceRes.StatusCode);

        using HttpResponseMessage first = await handle.Client.PostAsync(
            "/api/mappings/add",
            JsonBody(new
            {
                tags = new[]
                {
                    new { sourceId = "kep", daItemId = "ns=2;s=A", displayName = "A", dataType = "Auto", uaNodeId = "" },
                    new { sourceId = "kep", daItemId = "ns=2;s=B", displayName = "B", dataType = "Auto", uaNodeId = "" }
                }
            }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using HttpResponseMessage second = await handle.Client.PostAsync(
            "/api/mappings/add",
            JsonBody(new
            {
                tags = new[]
                {
                    new { sourceId = "kep", daItemId = "ns=2;s=C", displayName = "C", dataType = "Auto", uaNodeId = "" }
                }
            }));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal("Source kep exceeds MaxMappedTags (2).", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostMappingsBulkAdd_OpcUa_ExceedsMaxMappedTags_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage sourceRes = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId = "kep",
                displayName = "Kepware",
                sourceType = "OpcUa",
                endpointUrl = "opc.tcp://127.0.0.1:49320",
                securityMode = "None",
                securityPolicy = "None",
                maxMappedTags = 1,
                updateRateMs = 1000
            }));
        Assert.Equal(HttpStatusCode.OK, sourceRes.StatusCode);

        using HttpResponseMessage bulk = await handle.Client.PostAsync(
            "/api/mappings/bulk-add",
            JsonBody(new
            {
                tags = new[]
                {
                    new { sourceId = "kep", daItemId = "ns=2;s=A", displayName = "A" },
                    new { sourceId = "kep", daItemId = "ns=2;s=B", displayName = "B" }
                }
            }));
        Assert.Equal(HttpStatusCode.BadRequest, bulk.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await bulk.Content.ReadAsStringAsync());
        Assert.Equal("Source kep exceeds MaxMappedTags (1).", body.RootElement.GetProperty("error").GetString());
    }
}
