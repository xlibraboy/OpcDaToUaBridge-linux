using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Verifies the extended /api/diagnostics payload consumed by the dashboard
/// Diagnostics tab: runtime summary, UA server totals, uptime, MQTT/InfluxDB
/// integration health, and problem lists. Shares one app instance for speed.
/// </summary>
public sealed class DiagnosticsApiTests : IAsyncLifetime
{
    private TestAppHandle? app_;

    public async Task InitializeAsync()
    {
        app_ = await TestAppHandle.StartAsync(dir =>
        {
            var appsettings = new
            {
                Da = new { ProgId = "Matrikon.OPC.Simulation.1", Host = "localhost", UpdateRateMs = 1000, UseSubscriptions = true },
                Ua = new { ApplicationName = "OpcBridge", EndpointUrl = "opc.tcp://0.0.0.0:4840/OpcBridge", AutoAcceptUntrustedCertificates = true, RequireAuthentication = false, Username = "", Password = "", AllowedIpAddresses = Array.Empty<string>() },
                Bridge = new { RateLimits = new { }, ExpectedTagCount = 100, Mappings = Array.Empty<object>() },
                Mqtt = new { Enabled = false, BrokerUrl = "tcp://localhost:1883", ClientId = "OpcBridge", UserName = (string?)null, Password = (string?)null, Tls = false, IgnoreCertErrors = false, TopicPrefix = "bridge/tags", PayloadFields = "Value, Timestamp" }
            };
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), JsonSerializer.Serialize(appsettings, new JsonSerializerOptions { WriteIndented = true }));
        });
    }

    public async Task DisposeAsync()
    {
        if (app_ is not null)
        {
            await app_.DisposeAsync();
        }
    }

    [Fact]
    public async Task Diagnostics_KeepsExistingBridgeAndUaSections()
    {
        using JsonDocument doc = await app_!.GetJsonAsync("/api/diagnostics");
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("bridge", out JsonElement bridge));
        Assert.True(bridge.TryGetProperty("staThreads", out _));
        Assert.True(bridge.TryGetProperty("writeQueue", out _));
        Assert.True(bridge.TryGetProperty("uaBandwidth", out _));
        Assert.True(root.TryGetProperty("ua", out JsonElement ua));
        Assert.True(ua.TryGetProperty("sessions", out _));
        Assert.True(ua.TryGetProperty("subscriptions", out _));
    }

    [Fact]
    public async Task Diagnostics_IncludesRuntimeSnapshot()
    {
        using JsonDocument doc = await app_!.GetJsonAsync("/api/diagnostics");
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("runtime", out JsonElement runtime), "expected top-level 'runtime'");
        Assert.True(runtime.TryGetProperty("bridgeState", out JsonElement bridgeState));
        Assert.Equal(JsonValueKind.String, bridgeState.ValueKind);
        Assert.True(runtime.TryGetProperty("daConnectionState", out _));
        Assert.True(runtime.TryGetProperty("updateRateMs", out _));
        Assert.True(runtime.TryGetProperty("mappingCount", out _));
        Assert.True(runtime.TryGetProperty("lastDaReadUtc", out _));
        Assert.True(runtime.TryGetProperty("lastDaReadCount", out _));
        Assert.True(runtime.TryGetProperty("lastUaWriteUtc", out _));
        Assert.True(runtime.TryGetProperty("lastUaWriteCount", out _));
        Assert.True(runtime.TryGetProperty("lastPollDurationMs", out _));
        Assert.True(runtime.TryGetProperty("lastPollValueRate", out _));
        Assert.True(runtime.TryGetProperty("sessionId", out _));
        Assert.True(runtime.TryGetProperty("interactiveSession", out _));
    }

    [Fact]
    public async Task Diagnostics_IncludesUaServerSummaryAndUptime()
    {
        using JsonDocument doc = await app_!.GetJsonAsync("/api/diagnostics");
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("uaServer", out JsonElement uaServer), "expected top-level 'uaServer'");
        Assert.True(uaServer.TryGetProperty("state", out _));
        Assert.True(uaServer.TryGetProperty("endpointUrl", out _));
        Assert.True(uaServer.TryGetProperty("connectedClientCount", out _));
        Assert.True(uaServer.TryGetProperty("mappedNodeCount", out _));

        Assert.True(root.TryGetProperty("uptimeSeconds", out JsonElement uptime), "expected top-level 'uptimeSeconds'");
        Assert.Equal(JsonValueKind.Number, uptime.ValueKind);
        Assert.True(uptime.GetDouble() >= 0);
    }

    [Fact]
    public async Task Diagnostics_IncludesMqttAndInfluxIntegrationHealth()
    {
        using JsonDocument doc = await app_!.GetJsonAsync("/api/diagnostics");
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("mqtt", out JsonElement mqtt), "expected top-level 'mqtt'");
        Assert.True(mqtt.TryGetProperty("enabled", out JsonElement mqttEnabled));
        Assert.Equal(false, mqttEnabled.GetBoolean()); // disabled in test appsettings
        Assert.True(mqtt.TryGetProperty("state", out _));
        Assert.True(mqtt.TryGetProperty("lastError", out _));
        Assert.True(mqtt.TryGetProperty("publishedCount", out _));
        Assert.True(mqtt.TryGetProperty("receivedCount", out _));
        Assert.True(mqtt.TryGetProperty("publishedRate", out _));
        Assert.True(mqtt.TryGetProperty("receivedRate", out _));

        Assert.True(root.TryGetProperty("influx", out JsonElement influx), "expected top-level 'influx'");
        Assert.True(influx.TryGetProperty("state", out _));
        Assert.True(influx.TryGetProperty("lastError", out _));
        Assert.True(influx.TryGetProperty("writtenCount", out _));
        Assert.True(influx.TryGetProperty("writtenRate", out _));
    }

    [Fact]
    public async Task Diagnostics_IncludesProblemsSection()
    {
        using JsonDocument doc = await app_!.GetJsonAsync("/api/diagnostics");
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("problems", out JsonElement problems), "expected top-level 'problems'");
        Assert.True(problems.TryGetProperty("disconnected", out JsonElement disconnected));
        Assert.Equal(JsonValueKind.Array, disconnected.ValueKind);
        Assert.True(problems.TryGetProperty("badQualityTotal", out JsonElement badTotal));
        Assert.Equal(JsonValueKind.Number, badTotal.ValueKind);
        Assert.True(badTotal.GetInt32() >= 0);
        Assert.True(problems.TryGetProperty("badQuality", out JsonElement badQuality));
        Assert.Equal(JsonValueKind.Array, badQuality.ValueKind);
    }
}
