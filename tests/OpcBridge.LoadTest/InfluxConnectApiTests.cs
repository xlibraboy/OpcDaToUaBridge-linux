using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Historian panel has to report what is really there. The writer used to mark itself
/// Connected as soon as it had built an HTTP client — no server required — so the dashboard
/// showed a healthy InfluxDB with nothing listening on the URL (issue #10). Connecting to an
/// unreachable server must now land as Faulted with the reason the operator can act on.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class InfluxConnectApiTests
{
    private static void WriteAppsettings(string dir)
    {
        var appsettings = new
        {
            Da = new { ProgId = "Matrikon.OPC.Simulation.1", Host = "localhost", UpdateRateMs = 1000, UseSubscriptions = true },
            Ua = new { ApplicationName = "OpcBridge", EndpointUrl = "opc.tcp://0.0.0.0:4840/OpcBridge", AutoAcceptUntrustedCertificates = true, RequireAuthentication = false, Username = "", Password = "", AllowedIpAddresses = Array.Empty<string>() },
            Bridge = new { RateLimits = new { }, ExpectedTagCount = 100, Mappings = Array.Empty<object>() },
            Mqtt = new { Enabled = false, BrokerUrl = "tcp://localhost:1883", ClientId = "OpcBridge", UserName = (string?)null, Password = (string?)null, Tls = false, IgnoreCertErrors = false, TopicPrefix = "bridge/tags", PayloadFields = "Value, Timestamp" },
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
        if (File.Exists(influxPath))
        {
            File.Delete(influxPath);
        }
    }

    private static StringContent JsonBody(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    private static async Task ConfigureUnreachableAsync(TestAppHandle handle)
    {
        // Port 9 is the discard port — nothing listens, so the probe is refused immediately.
        using HttpResponseMessage saved = await handle.Client.PostAsync(
            "/api/influx/config",
            JsonBody(new
            {
                enabled = false,
                url = "http://127.0.0.1:9",
                org = "demo-org",
                bucket = "demo-bucket",
                token = "demo-token",
                measurement = "opc_tags",
                timeoutMs = 1000,
                verifySsl = false
            }));
        saved.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Connect_UnreachableUrl_ReportsFaultedWithTheReason()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteAppsettings);
        await ConfigureUnreachableAsync(handle);

        using HttpResponseMessage connect = await handle.Client.PostAsync("/api/influx/connect", null);
        using JsonDocument payload = JsonDocument.Parse(await connect.Content.ReadAsStringAsync());

        Assert.Equal("error", payload.RootElement.GetProperty("status").GetString());
        string? error = payload.RootElement.GetProperty("error").GetString();
        Assert.NotNull(error);
        Assert.Contains("127.0.0.1:9", error);

        using JsonDocument status = await handle.GetJsonAsync("/api/influx/status");
        Assert.Equal("Faulted", status.RootElement.GetProperty("state").GetString());
        Assert.Contains("127.0.0.1:9", status.RootElement.GetProperty("lastError").GetString() ?? string.Empty);
        // Nothing was written, so the counters must stay at zero.
        Assert.Equal(0, status.RootElement.GetProperty("writtenCount").GetInt64());
    }

    [Fact]
    public async Task Connect_UnreachableUrl_ThenDisconnect_ReturnsToDisconnected()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteAppsettings);
        await ConfigureUnreachableAsync(handle);

        using (HttpResponseMessage connect = await handle.Client.PostAsync("/api/influx/connect", null))
        {
            using JsonDocument payload = JsonDocument.Parse(await connect.Content.ReadAsStringAsync());
            Assert.Equal("error", payload.RootElement.GetProperty("status").GetString());
        }

        using HttpResponseMessage disconnect = await handle.Client.PostAsync("/api/influx/disconnect", null);
        Assert.Equal(HttpStatusCode.OK, disconnect.StatusCode);

        using JsonDocument status = await handle.GetJsonAsync("/api/influx/status");
        Assert.Equal("Disconnected", status.RootElement.GetProperty("state").GetString());
    }
}
