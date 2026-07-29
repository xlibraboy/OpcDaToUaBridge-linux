using System.Net;
using System.Text;
using System.Text.Json;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(DaLinkApiAppCollection))]
public sealed class MelsecApiTests
{
    [Fact]
    public async Task PostMelsecSource_GetSourcesReturnsSerialFields()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "lineA3n",
                displayName = "Line A3N",
                sourceType = "MelsecA3n",
                transport = "Serial",
                serialPortName = "/dev/ttyUSB0",
                baudRate = 9600,
                dataBits = 8,
                parity = "Odd",
                stopBits = "One",
                stationNo = "00",
                pcNo = "FF",
                timeoutMs = 3000,
                retryCount = 2,
                maxMappedTags = 500,
                updateRateMs = 1000
            }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using JsonDocument get = await app.GetJsonAsync("/api/da/sources");
        JsonElement source = Single(get.RootElement.GetProperty("sources"), "lineA3n");

        Assert.Equal("MelsecA3n", source.GetProperty("sourceType").GetString());
        Assert.Equal("/dev/ttyUSB0", source.GetProperty("serialPortName").GetString());
        Assert.Equal("Serial", source.GetProperty("transport").GetString());
        Assert.Equal(9600, source.GetProperty("baudRate").GetInt32());
        Assert.Equal(500, source.GetProperty("maxMappedTags").GetInt32());
    }

    [Fact]
    public async Task PostMelsecSource_MissingSerialPortName_Returns400()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "noport",
                sourceType = "MelsecA3n",
                transport = "Serial"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        string body = await post.Content.ReadAsStringAsync();
        Assert.Contains("SerialPortName", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostMelsecSource_TcpTunnelTransport_Returns400()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "badtransport",
                sourceType = "MelsecA3n",
                transport = "TcpTunnel",
                serialPortName = "/dev/ttyUSB0"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task PostMelsecSource_DuplicateSerialPort_Returns400()
    {
        await using TestAppHandle app = await StartWithMelsecSource("first", "/dev/ttyUSB0");

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "second",
                sourceType = "MelsecA3n",
                transport = "Serial",
                serialPortName = "/dev/ttyUSB0"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        string body = await post.Content.ReadAsStringAsync();
        Assert.Contains("SerialPortName", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostMelsecSource_DuplicateSerialPort_OnSameSourceUpdate_IsAllowed()
    {
        // Updating the same sourceId keeps its own port; not a conflict.
        await using TestAppHandle app = await StartWithMelsecSource("only", "/dev/ttyUSB0");

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "only",
                sourceType = "MelsecA3n",
                transport = "Serial",
                serialPortName = "/dev/ttyUSB0",
                baudRate = 19200
            }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
    }

    [Fact]
    public async Task ParseAddress_Valid_ReturnsCanonical()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/drivers/melsec-a3n/parse-address",
            Json(new { address = "d0" }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("D0", doc.RootElement.GetProperty("canonical").GetString());
    }

    [Fact]
    public async Task ParseAddress_Invalid_ReturnsError()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/drivers/melsec-a3n/parse-address",
            Json(new { address = "Z99" }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task MappingAdd_InvalidMelsecAddress_Returns400()
    {
        await using TestAppHandle app = await StartWithMelsecSource("lineA3n", "/dev/ttyUSB0");

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/mappings/add",
            Json(new
            {
                tags = new[]
                {
                    new { sourceId = "lineA3n", daItemId = "Z99" }
                }
            }));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        string body = await post.Content.ReadAsStringAsync();
        Assert.Contains("Z99", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappingAdd_ValidMelsecAddress_Canonicalizes()
    {
        await using TestAppHandle app = await StartWithMelsecSource("lineA3n", "/dev/ttyUSB0");

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/mappings/add",
            Json(new
            {
                tags = new[]
                {
                    new { sourceId = "lineA3n", daItemId = "d0" }
                }
            }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using JsonDocument get = await app.GetJsonAsync("/api/mappings");
        JsonElement mapping = Single(get.RootElement.GetProperty("mappings"), "lineA3n", "D0");
        Assert.Equal("D0", mapping.GetProperty("daItemId").GetString());
    }

    [Fact]
    public async Task MappingAdd_ExceedsMaxMappedTags_Returns400()
    {
        await using TestAppHandle app = await StartWithMelsecSource(
            "lineA3n", "/dev/ttyUSB0", maxMappedTags: 1);

        // First add succeeds (count 1).
        using HttpResponseMessage first = await app.Client.PostAsync(
            "/api/mappings/add",
            Json(new
            {
                tags = new[]
                {
                    new { sourceId = "lineA3n", daItemId = "D0" }
                }
            }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second add exceeds the limit of 1.
        using HttpResponseMessage second = await app.Client.PostAsync(
            "/api/mappings/add",
            Json(new
            {
                tags = new[]
                {
                    new { sourceId = "lineA3n", daItemId = "D1" }
                }
            }));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        string body = await second.Content.ReadAsStringAsync();
        Assert.Contains("max", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappingBulkAdd_ExceedsMaxMappedTags_Returns400()
    {
        await using TestAppHandle app = await StartWithMelsecSource(
            "lineA3n", "/dev/ttyUSB0", maxMappedTags: 2);

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/mappings/bulk-add",
            Json(new
            {
                tags = new[]
                {
                    new { sourceId = "lineA3n", daItemId = "D0" },
                    new { sourceId = "lineA3n", daItemId = "D1" },
                    new { sourceId = "lineA3n", daItemId = "D2" }
                }
            }));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task MappingUpdate_InvalidMelsecAddress_Returns400()
    {
        await using TestAppHandle app = await StartWithMelsecSource("lineA3n", "/dev/ttyUSB0");

        // Seed a valid mapping first.
        await app.Client.PostAsync(
            "/api/mappings/add",
            Json(new { tags = new[] { new { sourceId = "lineA3n", daItemId = "D0" } } }));

        using HttpResponseMessage update = await app.Client.PostAsync(
            "/api/mappings/update",
            Json(new
            {
                tag = new { sourceId = "lineA3n", daItemId = "Z99" }
            }));

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task GetStatus_MelsecSource_IncludesSourceTypeAndEndpointSummary()
    {
        await using TestAppHandle app = await StartWithMelsecSource("lineA3n", "/dev/ttyUSB0", baudRate: 19200);

        using JsonDocument status = await app.GetJsonAsync("/api/status");
        JsonElement source = Single(status.RootElement.GetProperty("bridge").GetProperty("sources"), "lineA3n");
        Assert.Equal("MelsecA3n", source.GetProperty("sourceType").GetString());
        Assert.Equal("/dev/ttyUSB0@19200", source.GetProperty("endpointSummary").GetString());
    }

    [Fact]
    public async Task GetStatus_OpcDaSource_IncludesSourceTypeAndEndpointSummary()
    {
        await using TestAppHandle app = await StartWithOpcDaSource("opc1", progId: "Matrikon.OPC.Simulation.1", host: "plchost");

        using JsonDocument status = await app.GetJsonAsync("/api/status");
        JsonElement source = Single(status.RootElement.GetProperty("bridge").GetProperty("sources"), "opc1");
        Assert.Equal("OpcDa", source.GetProperty("sourceType").GetString());
        Assert.Equal("plchost/Matrikon.OPC.Simulation.1", source.GetProperty("endpointSummary").GetString());
    }

    [Fact]
    public async Task ExportImport_MelsecSource_RoundTripsSerialFields()
    {
        string exported;
        await using (TestAppHandle srcApp = await StartWithMelsecSource("lineA3n", "/dev/ttyUSB0", baudRate: 19200, maxMappedTags: 500))
        {
            using JsonDocument export = await srcApp.GetJsonAsync("/api/config/export");
            JsonElement exportedSource = Single(export.RootElement.GetProperty("daSources").GetProperty("sources"), "lineA3n");
            Assert.Equal("MelsecA3n", exportedSource.GetProperty("sourceType").GetString());
            Assert.Equal("Serial", exportedSource.GetProperty("transport").GetString());
            Assert.Equal("/dev/ttyUSB0", exportedSource.GetProperty("serialPortName").GetString());
            Assert.Equal(19200, exportedSource.GetProperty("baudRate").GetInt32());
            Assert.Equal(8, exportedSource.GetProperty("dataBits").GetInt32());
            Assert.Equal("Odd", exportedSource.GetProperty("parity").GetString());
            Assert.Equal("One", exportedSource.GetProperty("stopBits").GetString());
            Assert.Equal("00", exportedSource.GetProperty("stationNo").GetString());
            Assert.Equal("FF", exportedSource.GetProperty("pcNo").GetString());
            Assert.Equal(3000, exportedSource.GetProperty("timeoutMs").GetInt32());
            Assert.Equal(2, exportedSource.GetProperty("retryCount").GetInt32());
            Assert.Equal(500, exportedSource.GetProperty("maxMappedTags").GetInt32());
            exported = export.RootElement.GetRawText();
        }

        // Import into a fresh app with no Melsec sources configured.
        await using TestAppHandle target = await TestAppHandle.StartAsync(_ => { });
        using HttpResponseMessage import = await target.Client.PostAsync(
            "/api/config/import",
            new StringContent(exported, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        using JsonDocument get = await target.GetJsonAsync("/api/da/sources");
        JsonElement source = Single(get.RootElement.GetProperty("sources"), "lineA3n");
        Assert.Equal("MelsecA3n", source.GetProperty("sourceType").GetString());
        Assert.Equal("Serial", source.GetProperty("transport").GetString());
        Assert.Equal("/dev/ttyUSB0", source.GetProperty("serialPortName").GetString());
        Assert.Equal(19200, source.GetProperty("baudRate").GetInt32());
        Assert.Equal("Odd", source.GetProperty("parity").GetString());
        Assert.Equal("One", source.GetProperty("stopBits").GetString());
        Assert.Equal(500, source.GetProperty("maxMappedTags").GetInt32());
    }

    [Fact]
    public async Task TestConnection_MissingPort_ReturnsError()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/drivers/melsec-a3n/test-connection",
            Json(new { }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    private static async Task<TestAppHandle> StartWithMelsecSource(
        string sourceId, string serialPort, int baudRate = 9600, int maxMappedTags = 2000)
    {
        return await TestAppHandle.StartAsync(dir =>
        {
            File.WriteAllText(
                Path.Combine(dir, "sources.json"),
                JsonSerializer.Serialize(new DaRuntimeSettingsSnapshot(
                    1000,
                    false,
                    new[]
                    {
                        new DaSourceRuntimeSettings(
                            sourceId,
                            sourceId,
                            SourceTypes.MelsecA3n,
                            1000,
                            true,
                            maxMappedTags,
                            null,
                            null,
                            new MelsecA3nSourceOptions(
                                "Serial",
                                serialPort,
                                baudRate,
                                8,
                                "Odd",
                                "One",
                                "00",
                                "FF",
                                3000,
                                2))
                    },
                    0)));
        });
    }

    private static async Task<TestAppHandle> StartWithOpcDaSource(
        string sourceId, string progId, string host)
    {
        return await TestAppHandle.StartAsync(dir =>
        {
            File.WriteAllText(
                Path.Combine(dir, "sources.json"),
                JsonSerializer.Serialize(new DaRuntimeSettingsSnapshot(
                    1000,
                    false,
                    new[]
                    {
                        new DaSourceRuntimeSettings(
                            sourceId,
                            sourceId,
                            SourceTypes.OpcDa,
                            1000,
                            true,
                            2000,
                            new OpcDaSourceOptions(progId, host, null, null, null),
                            null,
                            null)
                    },
                    0)));
        });
    }

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

    private static JsonElement Single(JsonElement array, string sourceId, string daItemId)
    {
        foreach (JsonElement el in array.EnumerateArray())
        {
            if (el.TryGetProperty("sourceId", out JsonElement sid) &&
                string.Equals(sid.GetString(), sourceId, StringComparison.OrdinalIgnoreCase) &&
                el.TryGetProperty("daItemId", out JsonElement did) &&
                string.Equals(did.GetString(), daItemId, StringComparison.OrdinalIgnoreCase))
            {
                return el;
            }
        }
        throw new Xunit.Sdk.XunitException($"Mapping '{sourceId}/{daItemId}' not found.");
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
