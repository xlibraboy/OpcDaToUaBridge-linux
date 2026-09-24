using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// End-to-end authentication and role enforcement. The test host opts into Auth
/// (TestAppHandle disables it by default and the per-test appsettings.json below
/// turns it back on), then each test signs in as a role and checks what the gate
/// actually lets through.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class AuthApiTests
{
    private static void WriteAuthAppsettings(string dir, bool trustHmi = true)
    {
        var appsettings = new
        {
            Da = new { SourceId = "default", DisplayName = "Sim Source", ProgId = "Matrikon.OPC.Simulation.1", Host = "localhost", UpdateRateMs = 1000, UseSubscriptions = true },
            Ua = new { ApplicationName = "OpcBridge", EndpointUrl = "opc.tcp://0.0.0.0:4840/OpcBridge", AutoAcceptUntrustedCertificates = true, RequireAuthentication = false, Username = "", Password = "", AllowedIpAddresses = Array.Empty<string>() },
            Bridge = new { RateLimits = new { }, ExpectedTagCount = 10, Mappings = Array.Empty<object>() },
            Mqtt = new { Enabled = false, BrokerUrl = "tcp://localhost:1883", ClientId = "OpcBridge", UserName = (string?)null, Password = (string?)null, Tls = false, IgnoreCertErrors = false, TopicPrefix = "bridge/tags", PayloadFields = "Value, Timestamp" },
            Auth = new { Enabled = true, SessionHours = 12, IdleMinutes = 45, TrustHmi = trustHmi }
        };
        File.WriteAllText(
            Path.Combine(dir, "appsettings.json"),
            JsonSerializer.Serialize(appsettings, new JsonSerializerOptions { WriteIndented = true }));

        string mapPath = Path.Combine(dir, "mappings.json");
        if (File.Exists(mapPath)) File.Delete(mapPath);
    }

    private static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    /// <summary>An HTTP client that carries one user's session cookie (cookies handled explicitly).</summary>
    private sealed class Session : IDisposable
    {
        private Session(HttpClient client, string? cookie)
        {
            Client = client;
            Cookie = cookie;
        }

        public HttpClient Client { get; }

        public string? Cookie { get; }

        public static async Task<Session> SignInAsync(Uri baseAddress, string username, string password)
        {
            HttpClient client = NewClient(baseAddress);
            using HttpResponseMessage response = await client.PostAsync(
                "/api/auth/login",
                Json(new { username, password }));

            string? cookie = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
                ? values.FirstOrDefault()?.Split(';')[0]
                : null;
            if (cookie is null)
            {
                client.Dispose();
                throw new Xunit.Sdk.XunitException(
                    $"Sign-in as {username} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }

            client.DefaultRequestHeaders.Add("Cookie", cookie);
            return new Session(client, cookie);
        }

        /// <summary>Client without cookies, for the anonymous cases.</summary>
        public static Session Anonymous(Uri baseAddress) => new(NewClient(baseAddress), null);

        private static HttpClient NewClient(Uri baseAddress) =>
            new(new HttpClientHandler { UseCookies = false }) { BaseAddress = baseAddress };

        public void Dispose() => Client.Dispose();
    }

    private static async Task CreateUserAsync(Session admin, string username, string password, string role)
    {
        using HttpResponseMessage response = await admin.Client.PostAsync(
            "/api/auth/users",
            Json(new { username, password, role, displayName = username }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> AddMappingAsync(HttpClient client) =>
        await client.PostAsync("/api/mappings/add", Json(new
        {
            tags = new[] { new { sourceId = "auth", itemId = "Auth.Tag1", dataType = "Auto", uaNodeId = "" } }
        }));

    [Fact]
    public async Task ShellAndHealth_ArePublic_ButApiDataIsNot()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteAuthAppsettings(dir));
        Uri baseAddress = handle.Client.BaseAddress!;

        using (HttpResponseMessage health = await handle.Client.GetAsync("/health"))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        using (HttpResponseMessage shell = await handle.Client.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
            // The sign-in card ships with the shell; without it a gated API is unreachable.
            Assert.Contains("authOverlay", await shell.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (HttpResponseMessage values = await handle.Client.GetAsync("/api/mappings"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, values.StatusCode);
        }

        using Session anonymous = Session.Anonymous(baseAddress);
        using (HttpResponseMessage me = await anonymous.Client.GetAsync("/api/auth/me"))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.True(body.RootElement.GetProperty("authEnabled").GetBoolean());
        }
    }

    [Fact]
    public async Task DefaultAdmin_SignsIn_AndGetsAFullPrivilegeSession()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteAuthAppsettings(dir));

        using Session admin = await Session.SignInAsync(handle.Client.BaseAddress!, "admin", "admin");
        Assert.StartsWith("opcbridge_session=", admin.Cookie!, StringComparison.Ordinal);

        using (HttpResponseMessage mappings = await admin.Client.GetAsync("/api/mappings"))
        {
            Assert.Equal(HttpStatusCode.OK, mappings.StatusCode);
        }

        using HttpResponseMessage me = await admin.Client.GetAsync("/api/auth/me");
        using JsonDocument body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal("Admin", body.RootElement.GetProperty("role").GetString());

        using HttpResponseMessage users = await admin.Client.GetAsync("/api/auth/users");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
    }

    [Fact]
    public async Task Me_ReportsTheIdleWindow_TheDashboardSignsOutOn()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteAuthAppsettings(dir));

        using Session admin = await Session.SignInAsync(handle.Client.BaseAddress!, "admin", "admin");
        using HttpResponseMessage me = await admin.Client.GetAsync("/api/auth/me");
        using JsonDocument body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());

        // The dashboard cannot guess it: its own poll would never let the server's window lapse.
        Assert.Equal(45, body.RootElement.GetProperty("idleMinutes").GetInt32());
    }

    [Fact]
    public async Task WrongPassword_IsRejected_AndRepeatedFailuresAreThrottled()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteAuthAppsettings(dir));

        HttpStatusCode last = HttpStatusCode.OK;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            using HttpResponseMessage response = await handle.Client.PostAsync(
                "/api/auth/login",
                Json(new { username = "admin", password = "wrong-" + attempt }));
            last = response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteAuthAppsettings(dir));

        using Session admin = await Session.SignInAsync(handle.Client.BaseAddress!, "admin", "admin");
        using (HttpResponseMessage before = await admin.Client.GetAsync("/api/mappings"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using HttpResponseMessage logout = await admin.Client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        // The cookie is still sent, but the server-side session is gone.
        using HttpResponseMessage after = await admin.Client.GetAsync("/api/mappings");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Viewer_ReadsButCannotConfigure()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteAuthAppsettings(dir));
        using Session admin = await Session.SignInAsync(handle.Client.BaseAddress!, "admin", "admin");
        await CreateUserAsync(admin, "viewer1", "secret", "Viewer");

        using Session viewer = await Session.SignInAsync(handle.Client.BaseAddress!, "viewer1", "secret");

        using (HttpResponseMessage read = await viewer.Client.GetAsync("/api/mappings"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        using (HttpResponseMessage write = await AddMappingAsync(viewer.Client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await write.Content.ReadAsStringAsync());
            Assert.Contains("Viewer", body.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
        }

        using HttpResponseMessage users = await viewer.Client.GetAsync("/api/auth/users");
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
    }

    [Fact]
    public async Task Operator_MayWriteTagValues_ButNotConfigure()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteAuthAppsettings(dir));
        using Session admin = await Session.SignInAsync(handle.Client.BaseAddress!, "admin", "admin");
        await CreateUserAsync(admin, "op1", "secret", "Operator");

        using Session op = await Session.SignInAsync(handle.Client.BaseAddress!, "op1", "secret");

        using (HttpResponseMessage write = await op.Client.PostAsync(
            "/api/hmi/write",
            Json(new { sourceId = "nope", itemId = "Nope.Tag", value = 1 })))
        {
            // Unknown source is a normal (200, ok=false) answer — the point is the gate let it through.
            Assert.NotEqual(HttpStatusCode.Forbidden, write.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, write.StatusCode);
        }

        using HttpResponseMessage add = await AddMappingAsync(op.Client);
        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
    }

    [Fact]
    public async Task Engineer_ConfiguresButCannotManageUsers()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteAuthAppsettings(dir));
        using Session admin = await Session.SignInAsync(handle.Client.BaseAddress!, "admin", "admin");
        await CreateUserAsync(admin, "eng1", "secret", "Engineer");

        using Session engineer = await Session.SignInAsync(handle.Client.BaseAddress!, "eng1", "secret");

        using (HttpResponseMessage add = await AddMappingAsync(engineer.Client))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        }

        using (HttpResponseMessage users = await engineer.Client.GetAsync("/api/auth/users"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
        }

        using (HttpResponseMessage create = await engineer.Client.PostAsync(
            "/api/auth/users",
            Json(new { username = "sneaky", password = "secret", role = "Admin" })))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }
    }

    [Fact]
    public async Task Admin_ManagesUsers_AndRemovalSignsThemOut()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteAuthAppsettings(dir));
        using Session admin = await Session.SignInAsync(handle.Client.BaseAddress!, "admin", "admin");
        await CreateUserAsync(admin, "temp1", "secret", "Operator");
        await CreateUserAsync(admin, "temp2", "secret", "Viewer");

        using Session target = await Session.SignInAsync(handle.Client.BaseAddress!, "temp1", "secret");
        using (HttpResponseMessage before = await target.Client.GetAsync("/api/auth/me"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        // Role change takes effect on the existing session, not only on the next login.
        using (HttpResponseMessage role = await admin.Client.PostAsync(
            "/api/auth/users/update",
            Json(new { username = "temp1", role = "Viewer" })))
        {
            Assert.Equal(HttpStatusCode.OK, role.StatusCode);
        }

        using (HttpResponseMessage denied = await AddMappingAsync(target.Client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (HttpResponseMessage removed = await admin.Client.PostAsync(
            "/api/auth/users/remove",
            Json(new { username = "temp1" })))
        {
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        }

        using (HttpResponseMessage afterRemoval = await target.Client.GetAsync("/api/auth/me"))
        {
            Assert.Equal(HttpStatusCode.OK, afterRemoval.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await afterRemoval.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("authenticated").GetBoolean());
        }

        // The last administrator cannot delete or demote themselves, and cannot be deleted by name either.
        using (HttpResponseMessage self = await admin.Client.PostAsync(
            "/api/auth/users/remove",
            Json(new { username = "admin" })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        }
    }

    [Fact]
    public async Task HmiEndpoints_StayOpen_WhileTrusted()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteAuthAppsettings(dir));

        using (HttpResponseMessage tags = await handle.Client.GetAsync("/api/hmi/tags"))
        {
            Assert.Equal(HttpStatusCode.OK, tags.StatusCode);
        }

        using (HttpResponseMessage negotiate = await handle.Client.PostAsync("/hmi/negotiate?negotiateVersion=1", null))
        {
            Assert.NotEqual(HttpStatusCode.Unauthorized, negotiate.StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, negotiate.StatusCode);
        }
    }

    [Fact]
    public async Task HmiEndpoints_RequireASession_WhenNotTrusted()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteAuthAppsettings(dir, trustHmi: false));

        using (HttpResponseMessage tags = await handle.Client.GetAsync("/api/hmi/tags"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, tags.StatusCode);
        }

        using Session admin = await Session.SignInAsync(handle.Client.BaseAddress!, "admin", "admin");
        using HttpResponseMessage authedTags = await admin.Client.GetAsync("/api/hmi/tags");
        Assert.Equal(HttpStatusCode.OK, authedTags.StatusCode);
    }
}
