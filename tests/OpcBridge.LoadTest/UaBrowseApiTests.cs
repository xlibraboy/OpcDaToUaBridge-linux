using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(InterlinkApiAppCollection))]
public sealed class UaBrowseApiTests
{
    private static void WriteMinimalAppsettings(string dir, string uaEndpoint = "opc.tcp://0.0.0.0:4840/OpcBridge")
    {
        var appsettings = new
        {
            Da = new { ProgId = "Matrikon.OPC.Simulation.1", Host = "localhost", UpdateRateMs = 1000, UseSubscriptions = true },
            Ua = new
            {
                ApplicationName = "OpcBridge",
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

    [Fact]
    public async Task TestConnection_MissingEndpoint_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/test-connection",
            JsonBody(new { securityMode = "None", securityPolicy = "None" }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        string? error = body.RootElement.GetProperty("error").GetString();
        Assert.Contains("endpoint", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Browse_MissingEndpoint_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/browse",
            JsonBody(new { nodeId = "i=85", maxNodes = 10 }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        string? error = body.RootElement.GetProperty("error").GetString();
        Assert.Contains("endpoint", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnection_UnknownSourceId_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/test-connection",
            JsonBody(new { sourceId = "does-not-exist" }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Contains("source", body.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Browse_DaSourceId_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/browse",
            JsonBody(new { sourceId = "default" }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        string? error = body.RootElement.GetProperty("error").GetString();
        Assert.Contains("OpcUa", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnection_InvalidSecurityPair_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/test-connection",
            JsonBody(new
            {
                endpointUrl = "opc.tcp://127.0.0.1:49320",
                securityMode = "Sign",
                securityPolicy = "None"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Contains("Security", body.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Browse_InvalidEndpointScheme_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/browse",
            JsonBody(new
            {
                endpointUrl = "http://127.0.0.1:49320",
                securityMode = "None",
                securityPolicy = "None"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Contains("opc.tcp", body.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnection_UnreachableEndpoint_ReturnsOkFalse()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        // Port unlikely to host a UA server; should not 400 — operational failure → ok:false.
        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/test-connection",
            JsonBody(new
            {
                endpointUrl = "opc.tcp://127.0.0.1:1",
                securityMode = "None",
                securityPolicy = "None"
            }));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Browse_UnreachableEndpoint_ReturnsErrorWithEmptyNodes()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/browse",
            JsonBody(new
            {
                endpointUrl = "opc.tcp://127.0.0.1:1",
                securityMode = "None",
                securityPolicy = "None",
                nodeId = "i=85",
                maxNodes = 5
            }));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("nodes").ValueKind);
        Assert.Equal(0, body.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Browse_SourceId_OpcUa_ResolvesConnectionFields()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage sourceRes = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId = "kep",
                displayName = "Kepware",
                sourceType = "OpcUa",
                endpointUrl = "opc.tcp://127.0.0.1:1",
                securityMode = "None",
                securityPolicy = "None"
            }));
        Assert.Equal(HttpStatusCode.OK, sourceRes.StatusCode);

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/ua/browse",
            JsonBody(new { sourceId = "kep", maxNodes = 5 }));

        // Source resolved; operational connect fails (port 1) but not validation 400.
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()));
    }
}
