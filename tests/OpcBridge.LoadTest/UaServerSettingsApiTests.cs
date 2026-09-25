using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Issue #14: the dashboard-facing surface of the UA server credential gate.
/// GET must report the access state without ever echoing the stored password;
/// POST must persist changes and refuse to enable the credential requirement
/// with a blank username/password pair (the server would lock everyone out).
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class UaServerSettingsApiTests
{
    private static void WriteMinimalAppsettings(string dir)
    {
        var appsettings = new
        {
            Da = new { ProgId = "Matrikon.OPC.Simulation.1", Host = "localhost", UpdateRateMs = 1000, UseSubscriptions = true },
            Ua = new
            {
                ApplicationName = "OpcBridge",
                EndpointUrl = "opc.tcp://0.0.0.0:4840/OpcBridge",
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
            System.Text.Json.JsonSerializer.Serialize(appsettings));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        string raw = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static StringContent JsonBody(object payload) =>
        new(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task GetSettings_ReportsAnonymousAccess_AndNeverEchoesPassword()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        HttpResponseMessage res = await handle.Client.GetAsync("/api/ua/settings");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        JsonElement body = await ReadJsonAsync(res);
        Assert.False(body.GetProperty("requireAuthentication").GetBoolean());
        Assert.Equal(string.Empty, body.GetProperty("username").GetString());
        Assert.False(body.TryGetProperty("password", out _), "The stored password must never be returned.");
    }

    [Fact]
    public async Task PostSettings_RequireCredentials_PersistsStateWithoutEchoingSecret()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        HttpResponseMessage res = await handle.Client.PostAsync("/api/ua/settings", JsonBody(new
        {
            requireAuthentication = true,
            username = "uaclient",
            password = "s3cret"
        }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        JsonElement body = await ReadJsonAsync(res);
        Assert.Contains("Restart the bridge to apply", body.GetProperty("message").GetString(), StringComparison.Ordinal);

        HttpResponseMessage after = await handle.Client.GetAsync("/api/ua/settings");
        JsonElement state = await ReadJsonAsync(after);
        Assert.True(state.GetProperty("requireAuthentication").GetBoolean());
        Assert.Equal("uaclient", state.GetProperty("username").GetString());
        Assert.False(state.TryGetProperty("password", out _), "The stored password must never be returned.");
    }

    [Fact]
    public async Task PostSettings_RequireOnWithoutCredentials_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        HttpResponseMessage res = await handle.Client.PostAsync("/api/ua/settings", JsonBody(new
        {
            requireAuthentication = true,
            username = "",
            password = ""
        }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        JsonElement body = await ReadJsonAsync(res);
        Assert.Contains("username and password", body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostSettings_RequireOff_LeavesCredentialsIntact()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        // Enable with credentials first.
        Assert.Equal(HttpStatusCode.OK,
            (await handle.Client.PostAsync("/api/ua/settings", JsonBody(new
            {
                requireAuthentication = true,
                username = "uaclient",
                password = "s3cret"
            }))).StatusCode);

        // Turning the requirement off (no credential fields in the body) keeps the
        // stored username/password so re-enabling later is a single toggle.
        Assert.Equal(HttpStatusCode.OK,
            (await handle.Client.PostAsync("/api/ua/settings", JsonBody(new { requireAuthentication = false }))).StatusCode);

        JsonElement state = await ReadJsonAsync(await handle.Client.GetAsync("/api/ua/settings"));
        Assert.False(state.GetProperty("requireAuthentication").GetBoolean());
        Assert.Equal("uaclient", state.GetProperty("username").GetString());
    }

    [Fact]
    public async Task PostSettings_BlankCredentialFields_MeanNoChange()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        Assert.Equal(HttpStatusCode.OK,
            (await handle.Client.PostAsync("/api/ua/settings", JsonBody(new
            {
                requireAuthentication = true,
                username = "uaclient",
                password = "s3cret"
            }))).StatusCode);

        // The dashboard clears the password box after every save, so a blank field
        // must read as "keep what is stored" — otherwise re-saving the same card
        // would wipe the secret and lock every client out.
        Assert.Equal(HttpStatusCode.OK,
            (await handle.Client.PostAsync("/api/ua/settings", JsonBody(new
            {
                requireAuthentication = true,
                username = "",
                password = ""
            }))).StatusCode);

        JsonElement state = await ReadJsonAsync(await handle.Client.GetAsync("/api/ua/settings"));
        Assert.True(state.GetProperty("requireAuthentication").GetBoolean());
        Assert.Equal("uaclient", state.GetProperty("username").GetString());
    }
}
