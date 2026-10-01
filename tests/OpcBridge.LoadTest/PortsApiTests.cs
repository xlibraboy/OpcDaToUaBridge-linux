using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The port surfaces the dashboard reads: GET /api/status/ports (per-address-family probes and
/// discovery-server detection, rendered by Monitor → Ports) plus the Port Configuration card's
/// /api/ports/config, /api/ports/probe and /api/firewall/* contracts. The behaviour of the probe
/// itself is pinned in <see cref="PortHelperTests"/>; here the contract the dashboard reads — and
/// what a save persists for the next start — is what matters.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
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

    [Fact]
    public async Task PortsConfig_Get_ReportsRunningSavedAndScanRanges()
    {
        using JsonDocument doc = await app_!.GetJsonAsync("/api/ports/config");
        JsonElement root = doc.RootElement;

        JsonElement running = root.GetProperty("running");
        int runningHttp = running.GetProperty("httpPort").GetInt32();
        Assert.True(runningHttp > 0);
        Assert.Equal(JsonValueKind.Number, running.GetProperty("uaPort").ValueKind);

        // The harness pins the ports in appsettings.json and both are free, so the saved values
        // are the running ones and no restart is pending.
        JsonElement saved = root.GetProperty("saved");
        Assert.Equal(runningHttp, saved.GetProperty("httpPort").GetInt32());
        Assert.Equal(running.GetProperty("uaPort").GetInt32(), saved.GetProperty("uaPort").GetInt32());
        Assert.Equal(JsonValueKind.String, saved.GetProperty("uaEndpointUrl").ValueKind);
        Assert.False(root.GetProperty("restartRequired").GetBoolean());

        JsonElement ranges = root.GetProperty("scanRanges");
        Assert.Equal(8080, ranges.GetProperty("httpStart").GetInt32());
        Assert.Equal(8180, ranges.GetProperty("httpEnd").GetInt32());
        Assert.Equal(4840, ranges.GetProperty("uaStart").GetInt32());
        Assert.Equal(4940, ranges.GetProperty("uaEnd").GetInt32());

        // Linux test host: the dashboard uses this flag to hide the firewall block.
        Assert.False(root.GetProperty("firewallSupported").GetBoolean());
    }

    [Fact]
    public async Task PortsConfig_Post_RejectsEqualAndOutOfRangePorts()
    {
        (HttpStatusCode equalStatus, JsonDocument equalBody) = await PostJsonAsync("/api/ports/config", new { httpPort = 8095, uaPort = 8095 });
        using (equalBody)
        {
            Assert.Equal(HttpStatusCode.BadRequest, equalStatus);
            Assert.Contains("different ports", equalBody.RootElement.GetProperty("error").GetString());
        }

        (HttpStatusCode rangeStatus, JsonDocument rangeBody) = await PostJsonAsync("/api/ports/config", new { httpPort = 0, uaPort = 4841 });
        using (rangeBody)
        {
            Assert.Equal(HttpStatusCode.BadRequest, rangeStatus);
        }
    }

    [Fact]
    public async Task PortsConfig_Post_RefusesABusyPortAndSuggestsTheNextFreeOne()
    {
        using TcpListener holder = new(IPAddress.Any, 0);
        holder.Start();
        int busyPort = ((IPEndPoint)holder.LocalEndpoint).Port;

        (HttpStatusCode status, JsonDocument body) = await PostJsonAsync("/api/ports/config", new { httpPort = busyPort, uaPort = ReserveFreePort() });
        using (body)
        {
            Assert.Equal(HttpStatusCode.Conflict, status);
            JsonElement busy = body.RootElement.GetProperty("busy");
            Assert.Equal(1, busy.GetArrayLength());
            Assert.Equal(busyPort, busy[0].GetProperty("port").GetInt32());
            Assert.Equal("IPv4", busy[0].GetProperty("heldFamilies").GetString());
            Assert.Equal(JsonValueKind.Number, body.RootElement.GetProperty("suggestion").GetProperty("httpPort").ValueKind);
        }
    }

    [Fact]
    public async Task PortsConfig_Post_SavesFreePortsForTheNextStartAndResetsTheCertificate()
    {
        int newHttp = ReserveFreePort();
        int newUa = ReserveFreePort();

        // A UA certificate from the running server: a UA port change re-issues it, the same way
        // startup auto-assignment does.
        string certPath = Path.Combine(app_!.AppDirectory, "pki", "own", "cert.der");
        Directory.CreateDirectory(Path.GetDirectoryName(certPath)!);
        await File.WriteAllTextAsync(certPath, "test");

        (HttpStatusCode status, JsonDocument body) = await PostJsonAsync("/api/ports/config", new { httpPort = newHttp, uaPort = newUa });
        using (body)
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(body.RootElement.GetProperty("restartRequired").GetBoolean());
            Assert.True(body.RootElement.GetProperty("certificateReset").GetBoolean());
            Assert.StartsWith("http://", body.RootElement.GetProperty("dashboardUrl").GetString());
            // Linux test host: no firewall rules to move.
            Assert.False(body.RootElement.GetProperty("firewall").GetProperty("applied").GetBoolean());
        }

        Assert.False(File.Exists(certPath));

        using JsonDocument settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(app_!.AppDirectory, "appsettings.json")));
        Assert.Equal(newHttp, settings.RootElement.GetProperty("Bridge").GetProperty("HttpPort").GetInt32());
        Assert.Equal(newUa, settings.RootElement.GetProperty("Bridge").GetProperty("OpcUaPort").GetInt32());
        // Ua:EndpointUrl is what actually binds, so the save must move its port too.
        Assert.Equal(newUa, new Uri(settings.RootElement.GetProperty("Ua").GetProperty("EndpointUrl").GetString()!).Port);

        // The running listeners do not move — the saved values apply on the next start.
        using JsonDocument config = await app_!.GetJsonAsync("/api/ports/config");
        Assert.True(config.RootElement.GetProperty("restartRequired").GetBoolean());
        Assert.Equal(newHttp, config.RootElement.GetProperty("saved").GetProperty("httpPort").GetInt32());
    }

    [Fact]
    public async Task PortsProbe_ReportsFamilyAvailabilitySuggestionsAndTheBridgesOwnPorts()
    {
        using TcpListener holder = new(IPAddress.Any, 0);
        holder.Start();
        int busyPort = ((IPEndPoint)holder.LocalEndpoint).Port;

        (HttpStatusCode busyStatus, JsonDocument busyBody) = await PostJsonAsync("/api/ports/probe", new { port = busyPort, kind = "http" });
        using (busyBody)
        {
            Assert.Equal(HttpStatusCode.OK, busyStatus);
            Assert.False(busyBody.RootElement.GetProperty("ipv4Free").GetBoolean());
            Assert.Equal("IPv4", busyBody.RootElement.GetProperty("heldFamilies").GetString());
            Assert.Equal(JsonValueKind.Number, busyBody.RootElement.GetProperty("suggestion").ValueKind);
            Assert.False(busyBody.RootElement.GetProperty("inUseByBridge").GetBoolean());
        }

        // The bridge's own listener is not a conflict — the card must not tell the operator to
        // move off the port the dashboard is served from.
        (HttpStatusCode ownStatus, JsonDocument ownBody) = await PostJsonAsync("/api/ports/probe", new { port = app_!.Client.BaseAddress!.Port, kind = "http" });
        using (ownBody)
        {
            Assert.Equal(HttpStatusCode.OK, ownStatus);
            Assert.True(ownBody.RootElement.GetProperty("inUseByBridge").GetBoolean());
        }

        int freePort = ReserveFreePort();
        (HttpStatusCode freeStatus, JsonDocument freeBody) = await PostJsonAsync("/api/ports/probe", new { port = freePort, kind = "ua" });
        using (freeBody)
        {
            Assert.Equal(HttpStatusCode.OK, freeStatus);
            Assert.True(freeBody.RootElement.GetProperty("ipv4Free").GetBoolean());
            Assert.Equal(JsonValueKind.Null, freeBody.RootElement.GetProperty("heldFamilies").ValueKind);
        }

        (HttpStatusCode badKindStatus, JsonDocument badKindBody) = await PostJsonAsync("/api/ports/probe", new { port = 12345, kind = "ftp" });
        using (badKindBody)
        {
            Assert.Equal(HttpStatusCode.BadRequest, badKindStatus);
        }
    }

    [Fact]
    public async Task Firewall_StatusAndApply_ReportUnsupportedOnTheLinuxTestHost()
    {
        using JsonDocument doc = await app_!.GetJsonAsync("/api/firewall/status");
        JsonElement root = doc.RootElement;
        // Present-but-false so the dashboard can distinguish "Windows host, no rules" from
        // "not a Windows host at all" (same rationale as the discoveryServer field).
        Assert.False(root.GetProperty("supported").GetBoolean());
        Assert.Equal(0, root.GetProperty("rules").GetArrayLength());

        (HttpStatusCode status, JsonDocument body) = await PostJsonAsync("/api/firewall/apply", new { });
        using (body)
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(body.RootElement.GetProperty("supported").GetBoolean());
            Assert.Equal(0, body.RootElement.GetProperty("results").GetArrayLength());
        }
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

    private async Task<(HttpStatusCode Status, JsonDocument Body)> PostJsonAsync(string path, object payload)
    {
        using StringContent content = new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await app_!.Client.PostAsync(path, content);
        string body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(body));
    }

    /// <summary>A port nothing is listening on: bound to port 0, read back, released.</summary>
    private static int ReserveFreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
