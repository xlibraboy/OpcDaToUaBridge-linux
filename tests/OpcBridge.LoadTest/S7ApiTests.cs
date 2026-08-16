using System.Net;
using System.Text;
using System.Text.Json;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(DaLinkApiAppCollection))]
public sealed class S7ApiTests
{
    [Fact]
    public async Task PostS7Source_GetSourcesReturnsPpiFields()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "lineS7",
                displayName = "Line S7",
                sourceType = "S7200Ppi",
                transport = "Serial",
                serialPortName = "/dev/ttyUSB1",
                baudRate = 9600,
                dataBits = 8,
                parity = "Even",
                stopBits = "One",
                localPpiAddress = 0,
                remotePpiAddress = 2,
                timeoutMs = 3000,
                retryCount = 2,
                maxMappedTags = 500,
                updateRateMs = 1000
            }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using JsonDocument get = await app.GetJsonAsync("/api/da/sources");
        JsonElement source = Single(get.RootElement.GetProperty("sources"), "lineS7");

        Assert.Equal("S7200Ppi", source.GetProperty("sourceType").GetString());
        Assert.Equal("/dev/ttyUSB1", source.GetProperty("serialPortName").GetString());
        Assert.Equal(9600, source.GetProperty("baudRate").GetInt32());
        Assert.Equal(0, source.GetProperty("localPpiAddress").GetInt32());
        Assert.Equal(2, source.GetProperty("remotePpiAddress").GetInt32());
        Assert.Equal(500, source.GetProperty("maxMappedTags").GetInt32());
    }

    [Fact]
    public async Task PostS7Source_MissingSerialPortName_Returns400()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "noport",
                sourceType = "S7200Ppi",
                transport = "Serial"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        string body = await post.Content.ReadAsStringAsync();
        Assert.Contains("SerialPortName", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostS7Source_TcpTunnelTransport_Returns400()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "tcp",
                sourceType = "S7200Ppi",
                transport = "TcpTunnel",
                serialPortName = "/dev/ttyUSB0"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        string body = await post.Content.ReadAsStringAsync();
        Assert.Contains("Serial", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostS7Source_DuplicatePortWithMelsec_Returns400()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage melsec = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "a3n",
                sourceType = "MelsecA3n",
                transport = "Serial",
                serialPortName = "/dev/ttyUSB9"
            }));
        Assert.Equal(HttpStatusCode.OK, melsec.StatusCode);

        using HttpResponseMessage s7 = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "s7",
                sourceType = "S7200Ppi",
                transport = "Serial",
                serialPortName = "/dev/ttyUSB9"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, s7.StatusCode);
    }

    [Fact]
    public async Task ParseAddress_Valid_ReturnsCanonical()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/drivers/s7200-ppi/parse-address",
            Json(new { address = "vw100" }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("VW100", doc.RootElement.GetProperty("canonical").GetString());
    }

    [Fact]
    public async Task ParseAddress_Invalid_Returns400()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/drivers/s7200-ppi/parse-address",
            Json(new { address = "T0" }));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task MappingUpsert_InvalidS7Address_Returns400()
    {
        await using TestAppHandle app = await StartWithS7Source("s7map", "/dev/ttyUSB2");

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/mappings/add",
            Json(new
            {
                tags = new[]
                {
                    new
                    {
                        sourceId = "s7map",
                        itemId = "T0",
                        dataType = "Bool",
                        enabled = true
                    }
                }
            }));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        string body = await post.Content.ReadAsStringAsync();
        Assert.Contains("S7", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappingUpsert_ValidS7Address_Canonicalizes()
    {
        await using TestAppHandle app = await StartWithS7Source("s7ok", "/dev/ttyUSB3");

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/mappings/add",
            Json(new
            {
                tags = new[]
                {
                    new
                    {
                        sourceId = "s7ok",
                        itemId = "vw10",
                        dataType = "Int16",
                        enabled = true
                    }
                }
            }));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using JsonDocument get = await app.GetJsonAsync("/api/mappings");
        JsonElement tags = get.RootElement.GetProperty("mappings");
        bool found = false;
        foreach (JsonElement t in tags.EnumerateArray())
        {
            if (t.GetProperty("sourceId").GetString() == "s7ok")
            {
                Assert.Equal("VW10", t.GetProperty("itemId").GetString());
                found = true;
            }
        }

        Assert.True(found);
    }

    [Fact]
    public async Task GetSerialPorts_ReturnsPortsArray()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using JsonDocument doc = await app.GetJsonAsync("/api/serial/ports");
        Assert.True(doc.RootElement.TryGetProperty("ports", out JsonElement ports));
        Assert.Equal(JsonValueKind.Array, ports.ValueKind);
    }

    private static async Task<TestAppHandle> StartWithS7Source(string sourceId, string port)
    {
        TestAppHandle app = await TestAppHandle.StartAsync(_ => { });
        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId,
                sourceType = "S7200Ppi",
                transport = "Serial",
                serialPortName = port,
                parity = "Even",
                localPpiAddress = 0,
                remotePpiAddress = 2
            }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        return app;
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static JsonElement Single(JsonElement sources, string sourceId)
    {
        foreach (JsonElement s in sources.EnumerateArray())
        {
            if (string.Equals(s.GetProperty("sourceId").GetString(), sourceId, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }

        throw new Xunit.Sdk.XunitException($"Source '{sourceId}' not found.");
    }
}
