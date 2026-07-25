using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(DaLinkApiAppCollection))]
public sealed class InfluxApiTests
{
    private static void WriteAppsettings(string dir)
    {
        var appsettings = new
        {
            Da = new { ProgId = "Matrikon.OPC.Simulation.1", Host = "localhost", UpdateRateMs = 1000, UseSubscriptions = true },
            Ua = new { ApplicationName = "OpcDaToUaBridge", EndpointUrl = "opc.tcp://0.0.0.0:4840/OpcBridge", AutoAcceptUntrustedCertificates = true, RequireAuthentication = false, Username = "", Password = "", AllowedIpAddresses = Array.Empty<string>() },
            Bridge = new { RateLimits = new { }, ExpectedTagCount = 100, Mappings = Array.Empty<object>() },
            Mqtt = new { Enabled = false, BrokerUrl = "tcp://localhost:1883", ClientId = "OpcDaToUaBridge", UserName = (string?)null, Password = (string?)null, Tls = false, IgnoreCertErrors = false, TopicPrefix = "bridge/tags", PayloadFields = "Value, Timestamp" },
            Influx = new
            {
                Enabled = false,
                Url = "http://localhost:8086",
                Org = "",
                Bucket = "",
                Token = (string?)null,
                Measurement = "opc_tags",
                TimeoutMs = 5000,
                VerifySsl = true
            }
        };
        File.WriteAllText(
            Path.Combine(dir, "appsettings.json"),
            JsonSerializer.Serialize(appsettings, new JsonSerializerOptions { WriteIndented = true }));
        string influxPath = Path.Combine(dir, "influx.json");
        if (File.Exists(influxPath)) File.Delete(influxPath);
    }

    [Fact]
    public async Task InfluxStatus_Returns_Disconnected_ByDefault()
    {
        await using var handle = await TestAppHandle.StartAsync(WriteAppsettings);

        using var status = await handle.GetJsonAsync("/api/influx/status");
        Assert.Equal("Disconnected", status.RootElement.GetProperty("state").GetString());
        Assert.False(status.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, status.RootElement.GetProperty("writtenCount").GetInt64());
    }

    [Fact]
    public async Task InfluxConfig_Post_Persists_EnabledFlag()
    {
        await using var handle = await TestAppHandle.StartAsync(WriteAppsettings);

        using StringContent body = new(
            JsonSerializer.Serialize(new
            {
                enabled = true,
                url = "http://influx.example:8086",
                org = "demo-org",
                bucket = "demo-bucket",
                token = "demo-token",
                measurement = "opc_tags",
                timeoutMs = 7000,
                verifySsl = false
            }),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage post = await handle.Client.PostAsync("/api/influx/config", body);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using var config = await handle.GetJsonAsync("/api/influx/config");
        Assert.True(config.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("http://influx.example:8086", config.RootElement.GetProperty("url").GetString());
        Assert.Equal("demo-org", config.RootElement.GetProperty("org").GetString());
        Assert.Equal("demo-bucket", config.RootElement.GetProperty("bucket").GetString());
        Assert.Equal("demo-token", config.RootElement.GetProperty("token").GetString());
        Assert.Equal("opc_tags", config.RootElement.GetProperty("measurement").GetString());
        Assert.Equal(7000, config.RootElement.GetProperty("timeoutMs").GetInt32());
        Assert.False(config.RootElement.GetProperty("verifySsl").GetBoolean());

        using var status = await handle.GetJsonAsync("/api/influx/status");
        Assert.True(status.RootElement.GetProperty("enabled").GetBoolean());
    }
}
