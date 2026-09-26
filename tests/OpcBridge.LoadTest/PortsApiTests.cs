using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// GET /api/status/ports carries the per-address-family port probes and the discovery-server
/// detection that Monitor → Ports renders. These are the first tests for that endpoint.
/// The behaviour of the probe itself is pinned in <see cref="PortHelperTests"/>; here the
/// contract the dashboard reads is what matters.
/// </summary>
public sealed class PortsApiTests : IAsyncLifetime
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
    public async Task Ports_ExposesAPerFamilyProbeForEachPort()
    {
        using JsonDocument doc = await app_!.GetJsonAsync("/api/status/ports");
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("httpProbe", out JsonElement httpProbe), "expected 'httpProbe'");
        AssertProbeShape(httpProbe);
        Assert.True(root.TryGetProperty("uaProbe", out JsonElement uaProbe), "expected 'uaProbe'");
        AssertProbeShape(uaProbe);
    }

    [Fact]
    public async Task Ports_KeepsTheFieldsThePortsCardAlreadyRead()
    {
        using JsonDocument doc = await app_!.GetJsonAsync("/api/status/ports");
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("httpPort", out JsonElement httpPort));
        Assert.Equal(JsonValueKind.Number, httpPort.ValueKind);
        Assert.True(root.TryGetProperty("uaPort", out JsonElement uaPort));
        Assert.Equal(JsonValueKind.Number, uaPort.ValueKind);
        Assert.True(root.TryGetProperty("httpDefault", out _));
        Assert.True(root.TryGetProperty("uaDefault", out _));
        Assert.True(root.TryGetProperty("httpAutoAssigned", out JsonElement httpAuto));
        Assert.Equal(JsonValueKind.False, httpAuto.ValueKind);
        Assert.True(root.TryGetProperty("uaAutoAssigned", out _));
        Assert.True(root.TryGetProperty("uaEndpointBind", out _));
        Assert.True(root.TryGetProperty("uaEndpointClient", out JsonElement uaClient));
        Assert.StartsWith("opc.tcp://", uaClient.GetString());
    }

    [Fact]
    public async Task Ports_ReportsTheDiscoveryServerField()
    {
        using JsonDocument doc = await app_!.GetJsonAsync("/api/status/ports");
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("discoveryServer", out JsonElement discoveryServer), "expected 'discoveryServer'");
        // No OPC UA Local Discovery Server on a Linux test host — the field must still be
        // present (and null) so the dashboard can distinguish "none" from "not reported".
        Assert.Equal(JsonValueKind.Null, discoveryServer.ValueKind);
    }

    private static void AssertProbeShape(JsonElement probe)
    {
        Assert.Equal(JsonValueKind.Object, probe.ValueKind);

        Assert.True(probe.TryGetProperty("ipv4Free", out JsonElement ipv4), "expected 'ipv4Free'");
        Assert.True(ipv4.ValueKind is JsonValueKind.True or JsonValueKind.False);

        Assert.True(probe.TryGetProperty("ipv6Free", out JsonElement ipv6), "expected 'ipv6Free'");
        // null means "this host has no IPv6 stack", which must be distinguishable from busy.
        Assert.True(ipv6.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null);
    }
}
