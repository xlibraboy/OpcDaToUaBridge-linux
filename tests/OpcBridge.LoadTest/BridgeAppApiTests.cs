using System.Net;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(DaLinkApiAppCollection))]
public sealed class BridgeAppApiTests
{
    [Fact]
    public async Task Dashboard_IncludesAppsField()
    {
        await using var handle = await TestAppHandle.StartAsync(dir =>
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

        using var dashboard = await handle.GetJsonAsync("/api/dashboard");
        Assert.True(dashboard.RootElement.TryGetProperty("apps", out var apps));
        Assert.True(apps.TryGetProperty("detectedCount", out var detectedCount));
        Assert.True(detectedCount.GetInt32() >= 1);
        Assert.True(apps.TryGetProperty("detectedApps", out var detectedApps));
        Assert.Equal(JsonValueKind.Array, detectedApps.ValueKind);

        // Verify local app is present
        bool foundLocal = false;
        foreach (var app in detectedApps.EnumerateArray())
        {
            if (app.TryGetProperty("isLocal", out var isLocal) && isLocal.GetBoolean())
            {
                foundLocal = true;
                break;
            }
        }
        Assert.True(foundLocal, "Expected at least one local app in detectedApps");
    }

    [Fact]
    public async Task AppInfo_ReturnsMachineName()
    {
        await using var handle = await TestAppHandle.StartAsync(dir =>
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

        using var appInfo = await handle.GetJsonAsync("/api/app-info");
        Assert.True(appInfo.RootElement.TryGetProperty("machineName", out var machineName));
        Assert.False(string.IsNullOrWhiteSpace(machineName.GetString()));
        Assert.True(appInfo.RootElement.TryGetProperty("name", out var name));
        Assert.Equal("OpcBridge.App", name.GetString());
    }
}
