using System.Net;
using System.Text;
using System.Text.Json;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(DaLinkApiAppCollection))]
public sealed class MxComponentApiTests
{
    [Fact]
    public async Task PostMxSource_GetSourcesReturnsLogicalStation()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "mx1",
                displayName = "A3N via MX",
                sourceType = "MxComponent",
                logicalStationNumber = 3,
                timeoutMs = 3000,
                retryCount = 2,
                maxMappedTags = 500,
                updateRateMs = 1000
            }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using JsonDocument get = await app.GetJsonAsync("/api/da/sources");
        JsonElement source = Single(get.RootElement.GetProperty("sources"), "mx1");

        Assert.Equal("MxComponent", source.GetProperty("sourceType").GetString());
        Assert.Equal(3, source.GetProperty("logicalStationNumber").GetInt32());
        Assert.Equal(500, source.GetProperty("maxMappedTags").GetInt32());
    }

    [Fact]
    public async Task PostMxSource_NoSerialPortRequired()
    {
        // Unlike MelsecA3n/S7200Ppi, MX Component needs no serial port — only a station.
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "mx2",
                sourceType = "MxComponent",
                logicalStationNumber = 1
            }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
    }

    [Fact]
    public async Task PostMxSource_OutOfRangeStation_Returns400()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "mxbad",
                sourceType = "MxComponent",
                logicalStationNumber = 4096
            }));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        string body = await post.Content.ReadAsStringAsync();
        Assert.Contains("LogicalStationNumber", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAddressRanges_ReturnsSharedMelsecCatalog()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using JsonDocument doc = await app.GetJsonAsync("/api/drivers/mx-component/address-ranges");
        JsonElement root = doc.RootElement;

        Assert.Equal("MxComponent", root.GetProperty("sourceType").GetString());

        JsonElement devices = root.GetProperty("devices");
        Assert.True(devices.GetArrayLength() >= 10, "expected the full MELSEC device catalog");

        JsonElement d = First(devices, "device", "D");
        Assert.Equal(0, d.GetProperty("min").GetInt32());
        Assert.Equal(1023, d.GetProperty("max").GetInt32());
        Assert.Equal("Word", d.GetProperty("signalType").GetString());
        Assert.Equal("Decimal", d.GetProperty("numberBase").GetString());
        Assert.True(d.GetProperty("bitSuffixAllowed").GetBoolean());
        Assert.Equal(15, d.GetProperty("maxBitIndex").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(d.GetProperty("example").GetString()));

        JsonElement x = First(devices, "device", "X");
        Assert.Equal("Bit", x.GetProperty("signalType").GetString());
        Assert.Equal("OctalOrHex", x.GetProperty("numberBase").GetString());
        Assert.False(x.GetProperty("bitSuffixAllowed").GetBoolean());
    }

    [Fact]
    public async Task TestConnection_NoStation_ReturnsError()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/drivers/mx-component/test-connection",
            Json(new { }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task MappingAdd_InvalidMelsecAddress_Returns400()
    {
        await using TestAppHandle app = await StartWithMxSource("mxmap", logicalStationNumber: 2);

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/mappings/add",
            Json(new
            {
                tags = new[]
                {
                    new { sourceId = "mxmap", itemId = "Z99" }
                }
            }));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        string body = await post.Content.ReadAsStringAsync();
        Assert.Contains("Z99", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappingAdd_ValidMelsecAddress_Canonicalizes()
    {
        await using TestAppHandle app = await StartWithMxSource("mxok", logicalStationNumber: 2);

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/mappings/add",
            Json(new
            {
                tags = new[]
                {
                    new { sourceId = "mxok", itemId = "d0" }
                }
            }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using JsonDocument get = await app.GetJsonAsync("/api/mappings");
        bool found = false;
        foreach (JsonElement tag in get.RootElement.GetProperty("mappings").EnumerateArray())
        {
            if (string.Equals(tag.GetProperty("sourceId").GetString(), "mxok", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal("D0", tag.GetProperty("itemId").GetString());
                found = true;
            }
        }

        Assert.True(found);
    }

    [Fact]
    public async Task GetStatus_MxSource_IncludesEndpointSummary()
    {
        // Seed at startup (like the Melsec status test) so BridgeState knows the source
        // before the first status snapshot.
        await using TestAppHandle app = await TestAppHandle.StartAsync(dir =>
        {
            File.WriteAllText(
                Path.Combine(dir, "sources.json"),
                JsonSerializer.Serialize(new DaRuntimeSettingsSnapshot(
                    1000,
                    false,
                    new[]
                    {
                        new DaSourceRuntimeSettings(
                            "mx1",
                            "mx1",
                            SourceTypes.MxComponent,
                            1000,
                            true,
                            2000,
                            null,
                            null,
                            null,
                            null,
                            new MxComponentSourceOptions(3, 3000, 2))
                    },
                    0)));
        });

        using JsonDocument status = await app.GetJsonAsync("/api/status");
        JsonElement source = Single(status.RootElement.GetProperty("bridge").GetProperty("sources"), "mx1");
        Assert.Equal("MxComponent", source.GetProperty("sourceType").GetString());
        Assert.Equal("MX station 3", source.GetProperty("endpointSummary").GetString());
    }

    [Fact]
    public async Task ExportImport_MxSource_RoundTripsLogicalStation()
    {
        string exported;
        await using (TestAppHandle srcApp = await StartWithMxSource("mx1", logicalStationNumber: 4, timeoutMs: 3500, retryCount: 1, maxMappedTags: 300))
        {
            using JsonDocument export = await srcApp.GetJsonAsync("/api/config/export");
            JsonElement exportedSource = Single(export.RootElement.GetProperty("daSources").GetProperty("sources"), "mx1");
            Assert.Equal("MxComponent", exportedSource.GetProperty("sourceType").GetString());
            Assert.Equal(4, exportedSource.GetProperty("logicalStationNumber").GetInt32());
            Assert.Equal(3500, exportedSource.GetProperty("timeoutMs").GetInt32());
            Assert.Equal(300, exportedSource.GetProperty("maxMappedTags").GetInt32());
            exported = export.RootElement.GetRawText();
        }

        await using TestAppHandle target = await TestAppHandle.StartAsync(_ => { });
        using HttpResponseMessage import = await target.Client.PostAsync(
            "/api/config/import",
            new StringContent(exported, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        using JsonDocument get = await target.GetJsonAsync("/api/da/sources");
        JsonElement source = Single(get.RootElement.GetProperty("sources"), "mx1");
        Assert.Equal("MxComponent", source.GetProperty("sourceType").GetString());
        Assert.Equal(4, source.GetProperty("logicalStationNumber").GetInt32());
        Assert.Equal(300, source.GetProperty("maxMappedTags").GetInt32());
    }

    private static async Task<TestAppHandle> StartWithMxSource(
        string sourceId,
        int logicalStationNumber,
        int timeoutMs = 3000,
        int retryCount = 2,
        int maxMappedTags = 2000)
    {
        TestAppHandle app = await TestAppHandle.StartAsync(_ => { });
        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId,
                sourceType = "MxComponent",
                logicalStationNumber,
                timeoutMs,
                retryCount,
                maxMappedTags,
                updateRateMs = 1000
            }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        return app;
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static JsonElement Single(JsonElement array, string sourceId)
    {
        foreach (JsonElement el in array.EnumerateArray())
        {
            if (el.TryGetProperty("sourceId", out JsonElement sid) &&
                string.Equals(sid.GetString(), sourceId, StringComparison.OrdinalIgnoreCase))
            {
                return el;
            }
        }

        throw new Xunit.Sdk.XunitException($"Source '{sourceId}' not found.");
    }

    private static JsonElement First(JsonElement array, string property, string value)
    {
        foreach (JsonElement el in array.EnumerateArray())
        {
            if (el.TryGetProperty(property, out JsonElement p) &&
                string.Equals(p.GetString(), value, StringComparison.Ordinal))
            {
                return el;
            }
        }

        throw new Xunit.Sdk.XunitException($"Element with {property}='{value}' not found.");
    }
}
