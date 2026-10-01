using System.Reflection;
using System.Diagnostics;
using System.IO.Ports;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.App.Auth;
using OpcBridge.App.Hmi;
using OpcBridge.Client;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Drivers.Melsec;
using OpcBridge.Drivers.Melsec.Addressing;
using OpcBridge.Drivers.S7;
using OpcBridge.Drivers.S7.Addressing;
using OpcBridge.Mqtt;
using OpcBridge.Influx;
using OpcBridge.Ua;

// Resolve the App-local data dir helper; OpcBridge.Ua has a same-named type.
using DataDirectory = OpcBridge.App.DataDirectory;

// Dashboard UI feed cap: the live-values payload is re-fetched and re-rendered every
// poll cycle; beyond this many values it freezes browsers. UI shows total separately.
const int DashboardValuesLimit = 2000;

// Registered before anything else can throw, so a startup failure is reported too. A bridge that
// dies must leave a reason behind — see CrashLog for the two unexplained stops that motivated it.
CrashLog.Install();

// ---- Single-instance guard: only one bridge may run per machine/user at a time ----
// A lock file is opened with FileShare.None and held for the process lifetime; the OS
// releases it automatically if the process exits or crashes, so there is no stale lock.
// OPCBRIDGE_INSTANCE_LOCK overrides the path (used by the test host to isolate instances).
string lockPath = Environment.GetEnvironmentVariable("OPCBRIDGE_INSTANCE_LOCK")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpcBridge",
        "bridge.lock");

try
{
    Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"OpcBridge: could not create instance-lock directory: {ex.Message}");
}

FileStream? acquiredLock = null;
try
{
    acquiredLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    acquiredLock.SetLength(0);
    byte[] pidBytes = System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
    acquiredLock.Write(pidBytes, 0, pidBytes.Length);
    acquiredLock.Flush();
}
catch (IOException)
{
    Console.Error.WriteLine(
        $"OpcBridge: another instance is already running (lock file: {lockPath}). " +
        "Refusing to start a second instance.");
    return;
}

using FileStream instanceLock = acquiredLock!;

// Port auto-assignment: check defaults, auto-roll if in use, persist to appsettings.json
(int savedHttp, int savedUa) = PortConfigStore.ReadBridgePorts(PortHelper.HttpScanStart, PortHelper.OpcUaScanStart);
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
ILogger logger = loggerFactory.CreateLogger("PortSetup");

// HTTP port: use saved value when free, else next free port in scan range
int httpPort = PortHelper.IsPortAvailable(savedHttp)
    ? savedHttp
    : PortHelper.FindAvailablePort(PortHelper.HttpScanStart, PortHelper.HttpScanEnd);
if (httpPort <= 0)
    throw new InvalidOperationException($"No available HTTP port in range {PortHelper.HttpScanStart}-{PortHelper.HttpScanEnd}.");

// OPC UA port: same strategy
int uaPort = PortHelper.IsPortAvailable(savedUa)
    ? savedUa
    : PortHelper.FindAvailablePort(PortHelper.OpcUaScanStart, PortHelper.OpcUaScanEnd);
if (uaPort <= 0)
    throw new InvalidOperationException($"No available OPC UA port in range {PortHelper.OpcUaScanStart}-{PortHelper.OpcUaScanEnd}.");

bool httpAuto = httpPort != savedHttp && savedHttp == PortHelper.HttpScanStart;
bool uaAuto = uaPort != savedUa && savedUa == PortHelper.OpcUaScanStart;

// Persist only when ports changed from the saved values
if (httpPort != savedHttp || uaPort != savedUa)
{
    PortConfigStore.Save(httpPort, uaPort);

    if (httpAuto)
        logger.LogWarning("HTTP port {Default} already in use. Auto-assigned to {Port}. appsettings.json updated.", PortHelper.HttpScanStart, httpPort);
    if (uaAuto)
        logger.LogWarning("OPC UA port {Default} already in use. Auto-assigned to {Port}. appsettings.json updated.", PortHelper.OpcUaScanStart, uaPort);

    // Force PKI cert regen when UA port changed
    if (uaAuto && PortConfigStore.DeleteUaCertificateIfPresent())
    {
        logger.LogInformation("Deleted pki/own/cert.der to trigger certificate regeneration with new UA port {Port}.", uaPort);
    }
}

BridgeState.ConfigurePorts(httpPort, uaPort, httpAuto, uaAuto);

// The installer writes the Start Menu dashboard shortcut with the port it was built with
// (8080); this is the only place that knows the port the bridge actually got, so keep the
// shortcut on it. Best effort — and a no-op where the MSI's shortcut is not installed.
DashboardShortcut.Update(httpPort, logger);

// Record which address families the chosen ports were already held on. This has to happen
// here: past this point Kestrel and the UA server bind, and the bridge's own listener would
// make its port read as taken. An IPv6-only holder — the OPC UA Local Discovery Server's
// [::]:4840 is the common one on a WinCC/Matrikon host — does not stop the IPv4 bind, so it
// is surfaced instead of triggering a port move that the installer's build-time firewall
// rule would not cover.
PortProbe httpProbe = PortHelper.Probe(httpPort);
PortProbe uaProbe = PortHelper.Probe(uaPort);
BridgeState.ConfigurePortReport(httpProbe, uaProbe);

if (httpProbe.HeldFamilies() is string httpHeld)
{
    logger.LogWarning("HTTP port {Port} is also held by another process on {Families}. Clients can reach that process instead of the bridge.", httpPort, httpHeld);
}

if (uaProbe.HeldFamilies() is string uaHeld)
{
    logger.LogWarning("OPC UA port {Port} is also held by another process on {Families}. A client on this machine using localhost can reach that process instead of the bridge.", uaPort, uaHeld);
}

// Windows session awareness: session-bound PLC simulators (GX Simulator's shared
// memory behind MX OPC, etc.) are only reachable from the interactive desktop
// session. A bridge launched into session 0 (SSH/WMI, services, S4U tasks) looks
// healthy but gets Bad values from those servers — surface it instead of failing
// silently. The dashboard shows a banner; the log warns here.
int sessionId = 0;
bool interactiveSession = true;
if (OperatingSystem.IsWindows())
{
    sessionId = Process.GetCurrentProcess().SessionId;
    interactiveSession = sessionId != 0;
    if (!interactiveSession)
    {
        logger.LogWarning(
            "Bridge is running in non-interactive Windows session {SessionId} (session 0). " +
            "Session-bound OPC DA servers (GX Simulator via MX OPC, or any simulator using " +
            "session-scoped shared memory) will not deliver values. Launch the bridge from the " +
            "interactive desktop session instead.",
            sessionId);
    }
}

BridgeState.ConfigureSession(sessionId, interactiveSession);
logger.LogInformation("Bridge listening on http://0.0.0.0:{HttpPort}", httpPort);
logger.LogInformation("OPC UA server endpoint: opc.tcp://0.0.0.0:{UaPort}/OpcBridge", uaPort);

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Run as a Windows service when launched by the SCM (the MSI installs the app that
// way). Interactive launches are unaffected: this is a no-op unless the parent
// process is services.exe. The service runs in session 0, so session-bound PLC
// simulators (GX Simulator via MX OPC) cannot deliver values — the dashboard shows
// the session banner, which names the desktop-session deployment to use instead.
if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService();
}

builder.WebHost.UseUrls($"http://0.0.0.0:{httpPort}");
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection("Bridge"));
builder.Services.Configure<DaClientOptions>(builder.Configuration.GetSection("Da"));
builder.Services.Configure<UaServerOptions>(builder.Configuration.GetSection("Ua"));
builder.Services.Configure<MqttBrokerOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<InfluxOptions>(builder.Configuration.GetSection("Influx"));
builder.Services.Configure<HmiOptions>(builder.Configuration.GetSection("Hmi"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<ProfileOptions>(builder.Configuration.GetSection("Profile"));
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<AuthSessionStore>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<DashboardLogStore>();
builder.Logging.Services.AddSingleton<ILoggerProvider, DashboardLogProvider>();
// Durable twin of the dashboard's in-memory ring buffer. As a Windows service the console goes
// nowhere, so without this the bridge has no log that outlives the process.
builder.Services.AddSingleton<FileLogStore>();
builder.Logging.Services.AddSingleton<ILoggerProvider, FileLogProvider>();


builder.Services.AddSingleton<DaRuntimeSettings>();
builder.Services.AddSingleton<SourceClientFactory>();
builder.Services.AddSingleton<BridgeState>();
builder.Services.AddSingleton<MappingStore>();
builder.Services.AddSingleton<InterlinkStore>();
builder.Services.AddSingleton<IInterlinkMetadataResolver>(sp => sp.GetRequiredService<BridgeWorker>());
builder.Services.AddSingleton<UaServerHost>();
builder.Services.AddSingleton<OpcUaBrowseService>();
builder.Services.AddSingleton<DiscoveryServerProbe>();
builder.Services.AddSingleton<IMqttBridge, MqttBridge>();
builder.Services.AddSingleton<MqttRuntimeSettings>();
builder.Services.AddSingleton<MqttValueStore>();
builder.Services.AddSingleton<IInfluxWriter, InfluxWriter>();
builder.Services.AddSingleton<InfluxRuntimeSettings>();
builder.Services.AddSingleton<BridgeWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BridgeWorker>());
 builder.Services.AddHostedService<OpcBridgeMonitor>();
 builder.Services.AddHttpClient("BridgeAppDiscovery", client => client.Timeout = TimeSpan.FromSeconds(2));
 builder.Services.AddSingleton(sp => new BridgeAppDiscovery(
     sp.GetRequiredService<DaRuntimeSettings>(),
     sp.GetRequiredService<IHttpClientFactory>(),
     sp.GetRequiredService<ILogger<BridgeAppDiscovery>>(),
     BridgeState.HttpPort));
 builder.Services.AddHostedService(sp => sp.GetRequiredService<BridgeAppDiscovery>());
builder.Services.AddSignalR();
builder.Services.AddSingleton<DisplayStore>();
builder.Services.AddHostedService<HmiBroadcastService>();
builder.Services.AddSingleton<IInfluxTrendQuery>(sp =>
{
    InfluxRuntimeSettings settings = sp.GetRequiredService<InfluxRuntimeSettings>();
    ILogger<InfluxFluxTrendQuery> logger = sp.GetRequiredService<ILogger<InfluxFluxTrendQuery>>();
    return new InfluxFluxTrendQuery(() => settings.GetOptions(), logger);
});


WebApplication app = builder.Build();

// Name the durable log up front. An operator investigating a stop needs to know the file exists
// before they think to look for it — the whole reason the 2026-09-29 stops went unexplained was
// that nobody knew there was anything to read.
app.Logger.LogInformation("Durable log: {LogPath}", app.Services.GetRequiredService<FileLogStore>().FilePath);

// Session + role gate: resolves the dashboard caller and answers 401/403 from the
// AuthPolicy table before any endpoint runs. A no-op unless Auth:Enabled is set.
app.UseMiddleware<RoleGateMiddleware>();

if (app.Services.GetRequiredService<IOptions<AuthOptions>>().Value.Enabled)
{
    UserStore authUsers = app.Services.GetRequiredService<UserStore>();
    app.Logger.LogInformation("Dashboard authentication is enabled (roles: Admin, Engineer, Operator, Viewer).");
    if (authUsers.SeededDefaultAdmin)
    {
        app.Logger.LogWarning(
            "users.json did not exist: seeded the default administrator '{User}' with password '{Password}'. " +
            "Sign in and change it under Ops > Users.",
            UserStore.DefaultAdminUsername,
            UserStore.DefaultAdminPassword);
    }
}

TryMigrateLegacyInterlinks(app);

// Wall-clock-independent tick captured at startup; powers uptimeSeconds on /api/diagnostics.
long processStartTickMs = Environment.TickCount64;

app.MapGet("/", (HttpContext ctx) => {
    ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    ctx.Response.Headers["Pragma"] = "no-cache";
    ctx.Response.Headers["Expires"] = "0";
    return Results.Bytes(System.Text.Encoding.UTF8.GetBytes(DashboardPage.FullHtml), "text/html; charset=utf-8");
});
app.MapGet("/api/values", (BridgeState state) => Results.Json(new { values = state.GetValues() }));
app.MapGet("/api/hmi/tags", (MappingStore mappingStore, BridgeState state, DaRuntimeSettings daSettings) =>
    Results.Json(HmiTagSnapshot.Build(mappingStore, state, daSettings.GetSnapshot())));
app.MapPost("/api/hmi/write", async (HmiWriteRequest request, BridgeWorker worker, CancellationToken ct) =>
{
    (bool ok, string? error) = await worker.TryHmiWriteAsync(
        request.SourceId ?? string.Empty,
        request.ItemId ?? string.Empty,
        request.Value,
        ct).ConfigureAwait(false);
    return Results.Json(new HmiWriteResponse { Ok = ok, Error = error });
});
app.MapGet("/api/hmi/trends", async (
    string? sourceId,
    string? itemId,
    DateTime? from,
    DateTime? to,
    int? maxPoints,
    IInfluxTrendQuery trends,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(itemId))
    {
        return Results.Json(
            new { error = "sourceId and itemId are required" },
            statusCode: StatusCodes.Status400BadRequest);
    }

    DateTime toUtc = (to ?? DateTime.UtcNow).ToUniversalTime();
    DateTime fromUtc = (from ?? toUtc.AddHours(-1)).ToUniversalTime();
    if (fromUtc > toUtc)
    {
        return Results.Json(
            new { error = "from must be less than or equal to to" },
            statusCode: StatusCodes.Status400BadRequest);
    }

    int limit = maxPoints ?? 500;
    if (limit < 10) limit = 10;
    if (limit > 2000) limit = 2000;

    HmiTrendResponse response = await trends.QueryAsync(
        sourceId.Trim(),
        itemId.Trim(),
        fromUtc,
        toUtc,
        limit,
        ct).ConfigureAwait(false);

    return Results.Json(response);
});
app.MapGet("/api/hmi/displays", (DisplayStore displayStore) =>
    Results.Json(new DisplayListResponse { Items = displayStore.List() }));
app.MapGet("/api/hmi/displays/{id}", (string id, DisplayStore displayStore) =>
{
    if (!DisplayStore.IsValidId(id))
    {
        return Results.Json(new { error = "invalid id" }, statusCode: StatusCodes.Status400BadRequest);
    }

    if (!displayStore.TryGet(id, out DisplayDocumentDto? document) || document is null)
    {
        return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
    }

    return Results.Json(document);
});
app.MapPut("/api/hmi/displays/{id}", async (string id, HttpRequest httpRequest, DisplayStore displayStore, CancellationToken ct) =>
{
    if (!DisplayStore.IsValidId(id))
    {
        return Results.Json(new { error = "invalid id" }, statusCode: StatusCodes.Status400BadRequest);
    }

    DisplayDocumentDto? body;
    try
    {
        body = await httpRequest.ReadFromJsonAsync<DisplayDocumentDto>(cancellationToken: ct).ConfigureAwait(false);
    }
    catch (JsonException)
    {
        return Results.Json(new { error = "invalid json" }, statusCode: StatusCodes.Status400BadRequest);
    }

    if (body is null)
    {
        return Results.Json(new { error = "body required" }, statusCode: StatusCodes.Status400BadRequest);
    }

    // Route id is authoritative.
    body.Id = id.Trim();
    DisplayPutResult result = displayStore.Put(body);
    return result.Status switch
    {
        DisplayPutStatus.Ok => Results.Json(result.Document),
        DisplayPutStatus.Conflict => Results.Json(
            new DisplayConflictResponse
            {
                Error = result.Error ?? "version conflict",
                CurrentVersion = result.CurrentVersion ?? 0
            },
            statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new { error = result.Error ?? "invalid document" }, statusCode: StatusCodes.Status400BadRequest)
    };
});
app.MapDelete("/api/hmi/displays/{id}", (string id, DisplayStore displayStore) =>
{
    if (!DisplayStore.IsValidId(id))
    {
        return Results.Json(new { error = "invalid id" }, statusCode: StatusCodes.Status400BadRequest);
    }

    if (!displayStore.Delete(id))
    {
        return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
    }

    return Results.NoContent();
});
 app.MapGet("/api/status", (BridgeState state, UaServerHost uaServer, BridgeAppDiscovery discovery) => Results.Json(new
 {
     bridge = state.GetStatus(),
     ua = uaServer.GetStatus(),
     apps = discovery.GetStatus(),
 }));

app.MapGet("/api/status/ports", (DiscoveryServerProbe discoveryProbe) =>
{
    string hostName = System.Net.Dns.GetHostName();
    string uaBind = $"opc.tcp://0.0.0.0:{BridgeState.UaPort}/OpcBridge";
    string uaClient = $"opc.tcp://{hostName}:{BridgeState.UaPort}/OpcBridge";
    return Results.Json(new BridgePorts(
        BridgeState.HttpPort,
        BridgeState.UaPort,
        PortHelper.HttpScanStart,
        PortHelper.OpcUaScanStart,
        BridgeState.HttpAutoAssigned,
        BridgeState.UaAutoAssigned,
        uaBind,
        uaClient,
        BridgeState.HttpPortProbe,
        BridgeState.UaPortProbe,
        discoveryProbe.Detect()));
});

// Port configuration (issue #26). The listeners bind once at startup, so a save persists the
// new ports for the next start and says so — the contract the UA Server Access card already
// uses. Values go to the data directory's appsettings.json (the file startup reads); a UA port
// change also moves the UA endpoint and re-issues the certificate, exactly like startup's
// auto-assignment does.
app.MapGet("/api/ports/config", () =>
{
    (int savedHttp, int savedUa) = PortConfigStore.ReadBridgePorts(PortHelper.HttpScanStart, PortHelper.OpcUaScanStart);
    // ua-settings.json (written by the UA Server Access card) overrides appsettings.json, so
    // the effective saved UA port is the one inside the endpoint URL that will bind.
    string? uaEndpointUrl = PortConfigStore.ReadUaEndpointUrl();
    int effectiveSavedUa = PortConfigStore.PortOf(uaEndpointUrl) ?? savedUa;

    return Results.Json(new
    {
        running = new
        {
            httpPort = BridgeState.HttpPort,
            uaPort = BridgeState.UaPort,
            httpAutoAssigned = BridgeState.HttpAutoAssigned,
            uaAutoAssigned = BridgeState.UaAutoAssigned
        },
        saved = new
        {
            httpPort = savedHttp,
            uaPort = effectiveSavedUa,
            uaEndpointUrl
        },
        restartRequired = savedHttp != BridgeState.HttpPort || effectiveSavedUa != BridgeState.UaPort,
        scanRanges = new
        {
            httpStart = PortHelper.HttpScanStart,
            httpEnd = PortHelper.HttpScanEnd,
            uaStart = PortHelper.OpcUaScanStart,
            uaEnd = PortHelper.OpcUaScanEnd
        },
        firewallSupported = WindowsFirewall.IsSupported
    });
});

// On-demand availability check for a candidate port: the startup probe only runs before the
// bridge binds, so the card needs this to check a port before saving it.
app.MapPost("/api/ports/probe", (PortProbeRequest request) =>
{
    if (request.Port is < 1 or > 65535)
    {
        return Results.BadRequest(new { error = "Port must be between 1 and 65535." });
    }

    string kind = (request.Kind ?? string.Empty).Trim().ToLowerInvariant();
    if (kind != "http" && kind != "ua")
    {
        return Results.BadRequest(new { error = "Kind must be 'http' or 'ua'." });
    }

    PortProbe probe = PortHelper.Probe(request.Port);
    int suggestion = kind == "http"
        ? PortHelper.FindAvailablePort(PortHelper.HttpScanStart, PortHelper.HttpScanEnd)
        : PortHelper.FindAvailablePort(PortHelper.OpcUaScanStart, PortHelper.OpcUaScanEnd);

    return Results.Json(new
    {
        port = request.Port,
        kind,
        ipv4Free = probe.Ipv4Free,
        ipv6Free = probe.Ipv6Free,
        heldFamilies = probe.HeldFamilies(),
        inUseByBridge = request.Port == BridgeState.HttpPort || request.Port == BridgeState.UaPort,
        suggestion = suggestion > 0 ? suggestion : (int?)null
    });
});

app.MapPost("/api/ports/config", async (PortConfigRequest request, UaServerHost uaServer, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (request.HttpPort is < 1 or > 65535 || request.UaPort is < 1 or > 65535)
    {
        return Results.BadRequest(new { error = "Ports must be between 1 and 65535." });
    }

    if (request.HttpPort == request.UaPort)
    {
        return Results.BadRequest(new { error = "HTTP and OPC UA must use different ports." });
    }

    // A port this bridge is already listening on reads as taken to the probe — it is ours, and
    // saving it back is how a restart re-applies the same port. An IPv6-only holder stays
    // allowed on purpose: the bridge binds IPv4, and moving off such a port would leave the
    // installer's firewall rule behind (PortHelper's documented policy).
    List<object> busy = new();
    PortProbe httpProbe = PortHelper.Probe(request.HttpPort);
    bool httpIsOurs = request.HttpPort == BridgeState.HttpPort || request.HttpPort == BridgeState.UaPort;
    if (!httpProbe.Ipv4Free && !httpIsOurs)
    {
        busy.Add(new { port = request.HttpPort, kind = "http", heldFamilies = httpProbe.HeldFamilies() });
    }

    PortProbe uaProbe = PortHelper.Probe(request.UaPort);
    bool uaIsOurs = request.UaPort == BridgeState.HttpPort || request.UaPort == BridgeState.UaPort;
    if (!uaProbe.Ipv4Free && !uaIsOurs)
    {
        busy.Add(new { port = request.UaPort, kind = "ua", heldFamilies = uaProbe.HeldFamilies() });
    }

    if (busy.Count > 0)
    {
        int httpSuggestion = PortHelper.FindAvailablePort(PortHelper.HttpScanStart, PortHelper.HttpScanEnd);
        int uaSuggestion = PortHelper.FindAvailablePort(PortHelper.OpcUaScanStart, PortHelper.OpcUaScanEnd);
        return Results.Json(
            new
            {
                error = "A port is already in use by another process.",
                busy,
                suggestion = new
                {
                    httpPort = httpSuggestion > 0 ? httpSuggestion : (int?)null,
                    uaPort = uaSuggestion > 0 ? uaSuggestion : (int?)null
                }
            },
            statusCode: StatusCodes.Status409Conflict);
    }

    (int previousHttp, int previousUa) = PortConfigStore.ReadBridgePorts(PortHelper.HttpScanStart, PortHelper.OpcUaScanStart);
    int effectivePreviousUa = PortConfigStore.PortOf(PortConfigStore.ReadUaEndpointUrl()) ?? previousUa;

    PortConfigStore.Save(request.HttpPort, request.UaPort);

    bool uaChanged = request.UaPort != effectivePreviousUa;
    bool certificateReset = false;
    if (uaChanged)
    {
        // In-memory options follow immediately, so a later UA Server Access save cannot write
        // the old endpoint back; ua-settings.json is rewritten only when it already exists.
        uaServer.SetEndpointUrl(PortConfigStore.PatchPortInUrl(uaServer.GetOptions().EndpointUrl, request.UaPort));
        certificateReset = PortConfigStore.DeleteUaCertificateIfPresent();
    }

    // The installer's firewall rules are pinned to the ports the MSI was built with, so a saved
    // port that differs from the running one needs its rule moved. Best-effort — a firewall
    // failure must not fail the save; the card also has an explicit Apply button.
    List<object> firewallResults = new();
    if (OperatingSystem.IsWindows())
    {
        if (request.HttpPort != BridgeState.HttpPort)
        {
            firewallResults.Add(await ApplyFirewallRuleAsync(
                WindowsFirewall.DashboardRuleName,
                WindowsFirewall.DashboardRuleDescription,
                request.HttpPort,
                cancellationToken).ConfigureAwait(false));
        }

        if (request.UaPort != BridgeState.UaPort)
        {
            firewallResults.Add(await ApplyFirewallRuleAsync(
                WindowsFirewall.UaRuleName,
                WindowsFirewall.UaRuleDescription,
                request.UaPort,
                cancellationToken).ConfigureAwait(false));
        }
    }

    bool restartRequired = request.HttpPort != BridgeState.HttpPort || request.UaPort != BridgeState.UaPort;
    string dashboardUrl = $"http://{System.Net.Dns.GetHostName()}:{request.HttpPort}/";
    string message = restartRequired
        ? $"Ports saved. Restart the bridge to apply them (MSI service: Restart-Service OpcBridge; scheduled task: restart the OpcBridge task), then open {dashboardUrl}."
        : "Ports saved. The bridge is already listening on these ports.";
    if (certificateReset)
    {
        message += " OPC UA clients must re-point to the new endpoint and re-trust the re-issued certificate.";
    }

    logger.LogInformation(
        "Port configuration saved from the dashboard: HTTP {PreviousHttp} → {HttpPort}, OPC UA {PreviousUa} → {UaPort}. Restart required: {RestartRequired}.",
        previousHttp,
        request.HttpPort,
        effectivePreviousUa,
        request.UaPort,
        restartRequired);

    return Results.Json(new
    {
        status = "ok",
        httpPort = request.HttpPort,
        uaPort = request.UaPort,
        restartRequired,
        dashboardUrl,
        certificateReset,
        firewall = new { applied = firewallResults.Count > 0, results = firewallResults },
        message
    });
});

// Windows Firewall state for the two rules the installer creates (Windows hosts only; the
// dashboard hides the firewall block elsewhere).
app.MapGet("/api/firewall/status", async (CancellationToken cancellationToken) =>
{
    if (!OperatingSystem.IsWindows())
    {
        return Results.Json(new
        {
            supported = false,
            platform = "non-windows",
            message = "Windows Firewall rules apply on Windows hosts only.",
            rules = Array.Empty<object>()
        });
    }

    (int savedHttp, int savedUa) = PortConfigStore.ReadBridgePorts(PortHelper.HttpScanStart, PortHelper.OpcUaScanStart);
    int effectiveSavedUa = PortConfigStore.PortOf(PortConfigStore.ReadUaEndpointUrl()) ?? savedUa;

    (string Name, int RunningPort, int SavedPort)[] wanted =
    {
        (WindowsFirewall.DashboardRuleName, BridgeState.HttpPort, savedHttp),
        (WindowsFirewall.UaRuleName, BridgeState.UaPort, effectiveSavedUa)
    };

    List<object> rules = new();
    foreach ((string name, int runningPort, int savedPort) in wanted)
    {
        WindowsFirewall.RuleStatus status = await WindowsFirewall.GetRuleAsync(name, cancellationToken).ConfigureAwait(false);
        rules.Add(new
        {
            name = status.Name,
            exists = status.Exists,
            enabled = status.Enabled,
            port = status.Port,
            anyPort = status.AnyPort,
            error = status.Error,
            matchesRunning = status.Exists && status.Port == runningPort,
            matchesSaved = status.Exists && status.Port == savedPort
        });
    }

    return Results.Json(new { supported = true, platform = "windows", rules });
});

app.MapPost("/api/firewall/apply", async (FirewallApplyRequest request, CancellationToken cancellationToken) =>
{
    if (!OperatingSystem.IsWindows())
    {
        return Results.Json(new
        {
            supported = false,
            message = "Windows Firewall rules apply on Windows hosts only.",
            results = Array.Empty<object>()
        });
    }

    int httpPort = request.HttpPort is > 0 and <= 65535 ? request.HttpPort.Value : BridgeState.HttpPort;
    int uaPort = request.UaPort is > 0 and <= 65535 ? request.UaPort.Value : BridgeState.UaPort;

    List<object> results = new()
    {
        await ApplyFirewallRuleAsync(WindowsFirewall.DashboardRuleName, WindowsFirewall.DashboardRuleDescription, httpPort, cancellationToken).ConfigureAwait(false),
        await ApplyFirewallRuleAsync(WindowsFirewall.UaRuleName, WindowsFirewall.UaRuleDescription, uaPort, cancellationToken).ConfigureAwait(false)
    };

    return Results.Json(new { supported = true, results });
});

 app.MapGet("/api/dashboard", (BridgeState state, UaServerHost uaServer, BridgeAppDiscovery discovery, MappingStore mappingStore, InterlinkStore interlinkStore, BridgeWorker worker, DaRuntimeSettings daSettings, int? limit, string? sourceId) =>
 {
     IReadOnlyList<BridgeValueSnapshot> values = state.GetValues(limit ?? DashboardValuesLimit, sourceId);

     // Resolve the displayed data type: the runtime type of the actual source
     // value wins; the mapping's configured type is the fallback (read path
     // only — keeps the per-value update hot path untouched).
     (IReadOnlyList<TagMapping> mappings, _) = mappingStore.GetSnapshot();
     Dictionary<string, string> dataTypeByKey = DashboardValues.BuildDataTypeLookup(mappings);

     // Effective update rate per tag: assigned named subscription (clamped ≥ 100 ms)
     // wins, else per-tag PollRateMs, else the source default.
     Dictionary<string, int> sourceRates = state.GetStatus().Sources
         .GroupBy(source => source.SourceId, StringComparer.OrdinalIgnoreCase)
         .ToDictionary(group => group.Key, group => group.First().UpdateRateMs, StringComparer.OrdinalIgnoreCase);
     DaRuntimeSettingsSnapshot daSnapshot = daSettings.GetSnapshot();
     Dictionary<string, IReadOnlyList<UaSubscriptionSettings>> uaSubscriptionsBySource = daSnapshot.Sources
         .Where(source => source.UaSubscriptions.Count > 0)
         .ToDictionary(source => source.SourceId, source => source.UaSubscriptions, StringComparer.OrdinalIgnoreCase);
     Dictionary<string, int> updateRateByKey = DashboardValues.BuildUpdateRateLookup(mappings, sourceRates, uaSubscriptionsBySource);

     // Per-interlink runtime health: derive each saved rule's status from its
     // endpoints' live state (provider value quality, consumer source connection)
     // plus the forwarding telemetry BridgeWorker records per write.
     DateTime nowUtc = DateTime.UtcNow;
     IReadOnlyDictionary<string, DaSourceStatusSnapshot> sourceStates = state.GetStatus().Sources
         .GroupBy(source => source.SourceId, StringComparer.OrdinalIgnoreCase)
         .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
     IReadOnlyDictionary<string, InterlinkStats> statsByKey = state.GetLinkStats();
     var linkStats = interlinkStore.GetSnapshot().Rules.Select(rule =>
     {
         statsByKey.TryGetValue(BridgeState.NormalizeKey(rule.ConsumerSourceId, rule.ConsumerItemId), out InterlinkStats? stats);
         InterlinkStats telemetry = stats ?? InterlinkStats.Empty;
         bool consumerConnected = sourceStates.TryGetValue(rule.ConsumerSourceId, out DaSourceStatusSnapshot? consumerStatus)
             && string.Equals(consumerStatus.ConnectionState, "Connected", StringComparison.OrdinalIgnoreCase);
         bool providerHasValue = state.TryGetSnapshot(rule.ProviderSourceId, rule.ProviderItemId, out BridgeValueSnapshot providerSnapshot);
         InterlinkHealth health = InterlinkStatusEvaluator.Derive(new InterlinkStatusInput(
             rule.Enabled,
             providerHasValue,
             providerHasValue && providerSnapshot.IsGood,
             consumerConnected,
             telemetry.Attempts,
             telemetry.Failures,
             telemetry.LastForwardUtc,
             telemetry.LastWriteSuccess,
             telemetry.LastError,
             nowUtc), out string? reason);
         return new
         {
             id = rule.Id,
             status = health.ToString().ToLowerInvariant(),
             reason,
             attempts = telemetry.Attempts,
             ok = telemetry.Successes,
             failed = telemetry.Failures,
             lastForwardUtc = telemetry.LastForwardUtc,
             lastError = telemetry.LastError
         };
     }).ToArray();

     return Results.Json(new
     {
         bridge = state.GetStatus(),
         ua = uaServer.GetStatus(),
         apps = discovery.GetStatus(),
         values = values.Select(value => new
         {
             sourceId = value.SourceId,
             itemId = value.ItemId,
             value = value.Value,
             timestampUtc = value.TimestampUtc,
             daQuality = value.DaQuality,
             isGood = value.IsGood,
             dataType = DashboardValues.ResolveDataType(value.Value, dataTypeByKey, value.SourceId, value.ItemId),
             updateRate = DashboardValues.LookupUpdateRate(updateRateByKey, value.SourceId, value.ItemId)
         }),
         valuesTotal = state.GetValueCount(sourceId),
         disconnected = worker.GetDisconnectedTags(),
         badQuality = state.GetBadQualityTags().Select(tag => new { sourceId = tag.SourceId, itemId = tag.ItemId }),
         linkStats
     });
 });
app.MapGet("/api/diagnostics", (BridgeWorker worker, UaServerHost uaServer, BridgeState state, MqttRuntimeSettings mqttSettings, InfluxRuntimeSettings influxSettings, ILogger<Program> logger) =>
{
    void LogSectionFailure(string name, Exception exception) =>
        logger.LogError(exception, "/api/diagnostics: section {Section} failed; omitting it from the payload", name);

    BridgeRuntimeStatus runtimeStatus = state.GetStatus();
    UaServerStatus uaStatus = uaServer.GetStatus();
    MqttRuntimeSnapshot mqttSnapshot = mqttSettings.GetSnapshot();
    InfluxRuntimeSnapshot influxSnapshot = influxSettings.GetSnapshot();
    IReadOnlyList<(string SourceId, string ItemId)> badQualityTags = state.GetBadQualityTags();
    return Results.Json(new
    {
        bridge = DiagnosticsSections.Safe("bridge", () => worker.GetDiagnostics(), ex => LogSectionFailure("bridge", ex)),
        ua = new
        {
            sessions = DiagnosticsSections.Safe("ua.sessions", () => uaServer.GetSessionDiagnostics(), ex => LogSectionFailure("ua.sessions", ex)),
            subscriptions = DiagnosticsSections.Safe("ua.subscriptions", () => uaServer.GetSubscriptionDiagnostics(), ex => LogSectionFailure("ua.subscriptions", ex))
        },
        runtime = new
        {
            bridgeState = runtimeStatus.BridgeState,
            daConnectionState = runtimeStatus.DaConnectionState,
            updateRateMs = runtimeStatus.UpdateRateMs,
            mappingCount = runtimeStatus.MappingCount,
            lastDaReadUtc = runtimeStatus.LastDaReadUtc,
            lastDaReadCount = runtimeStatus.LastDaReadCount,
            lastUaWriteUtc = runtimeStatus.LastUaWriteUtc,
            lastUaWriteCount = runtimeStatus.LastUaWriteCount,
            lastPollDurationMs = runtimeStatus.LastPollDurationMs,
            lastPollValueRate = runtimeStatus.LastPollValueRate,
            sessionId = runtimeStatus.SessionId,
            interactiveSession = runtimeStatus.InteractiveSession
        },
        uaServer = new
        {
            state = uaStatus.State,
            endpointUrl = uaStatus.EndpointUrl,
            connectedClientCount = uaStatus.ConnectedClientCount,
            mappedNodeCount = uaStatus.MappedNodeCount
        },
        uptimeSeconds = Math.Round((Environment.TickCount64 - processStartTickMs) / 1000.0, 1),
        mqtt = new
        {
            enabled = mqttSnapshot.Options.Enabled,
            state = mqttSnapshot.State,
            lastError = mqttSnapshot.LastError,
            publishedCount = mqttSnapshot.PublishedCount,
            receivedCount = mqttSnapshot.ReceivedCount,
            publishedRate = mqttSnapshot.PublishedRate,
            receivedRate = mqttSnapshot.ReceivedRate
        },
        influx = new
        {
            enabled = influxSnapshot.Options.Enabled,
            state = influxSnapshot.State,
            lastError = influxSnapshot.LastError,
            writtenCount = influxSnapshot.WrittenCount,
            writtenRate = influxSnapshot.WrittenRate
        },
        problems = new
        {
            disconnected = DiagnosticsSections.Safe("problems.disconnected", () => worker.GetDisconnectedTags().Select(t => new { t.SourceId, t.ItemId }), ex => LogSectionFailure("problems.disconnected", ex)),
            badQualityTotal = badQualityTags.Count,
            badQuality = badQualityTags.Take(50).Select(t => new { t.SourceId, t.ItemId })
        }
    });
});
app.MapGet("/api/logs", (DashboardLogStore logStore, int? limit, string? level) =>
{
    LogLevel? minimumLevel = TryParseLogLevel(level, out LogLevel parsedLevel)
        ? parsedLevel
        : null;

    IReadOnlyList<DashboardLogEntry> entries = logStore.GetEntries(limit ?? 200, minimumLevel);
    return Results.Json(new
    {
        entries = entries.Select(entry => new
        {
            timestampUtc = entry.TimestampUtc,
            level = entry.Level.ToString(),
            category = entry.Category,
            message = entry.Message,
            exceptionText = entry.ExceptionText
        })
    });
});
 app.MapGet("/api/app-info", (IOptions<ProfileOptions> profile) =>
 {
     var info = AppInfoSnapshot.CreateCurrent(profile.Value.Creator, profile.Value.Section);
     return Results.Json(new
     {
         name = info.Name,
         version = info.Version,
         informationalVersion = info.InformationalVersion,
         framework = info.Framework,
         processArchitecture = info.ProcessArchitecture,
         osDescription = info.OsDescription,
         machineName = info.MachineName,
        creator = info.Creator,
        section = info.Section
     });
 });
app.MapGet("/api/version", () =>
{
    Assembly assembly = typeof(Program).Assembly;
    return Results.Json(new
    {
        version = assembly.GetName().Version?.ToString() ?? "0.0.0.0",
        informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty
    });
});
app.MapGet("/api/help", () => Results.Json(new { markdown = HelpContent.Markdown }));
// Release notes: the embedded CHANGELOG.md, rendered by the dashboard's Help > Release Notes view.
app.MapGet("/api/changelog", () => Results.Json(new
{
    markdown = ChangelogContent.Markdown,
    version = ChangelogContent.LatestVersion
}));
// Issues: the embedded ISSUES.md, rendered by the dashboard's Ops > Issues view (Admin only).
app.MapGet("/api/issues", () => Results.Json(new { markdown = IssuesContent.Markdown }));
app.MapGet("/api/da/sources", (DaRuntimeSettings settings) =>
{
    DaRuntimeSettingsSnapshot snapshot = settings.GetSnapshot();
    return Results.Json(new
    {
        updateRateMs = snapshot.UpdateRateMs,
        useSubscriptions = snapshot.UseSubscriptions,
        sources = snapshot.Sources.Select(ToSourceApiDto)
    });
});
app.MapPost("/api/da/update-rate", (DaUpdateRateRequest request, DaRuntimeSettings settings) =>
{
    if (request.UpdateRateMs != DaRuntimeSettings.FixedUpdateRateMs)
    {
        return Results.BadRequest(new { error = "Default update rate is fixed at 1000 ms; use per-tag rates for other cadences." });
    }

    DaRuntimeSettingsSnapshot snapshot = settings.SetUpdateRate(request.UpdateRateMs);
    return Results.Json(new
    {
        version = snapshot.Version,
        updateRateMs = snapshot.UpdateRateMs
    });
});
app.MapPost("/api/da/use-subscriptions", (DaUseSubscriptionsRequest request, DaRuntimeSettings settings) =>
{
    DaRuntimeSettingsSnapshot snapshot = settings.SetUseSubscriptions(request.UseSubscriptions);
    return Results.Json(new
    {
        version = snapshot.Version,
        useSubscriptions = snapshot.UseSubscriptions
    });
});
app.MapPost("/api/da/sources/update-rate", (DaSourceUpdateRateRequest request, DaRuntimeSettings settings) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceId))
    {
        return Results.BadRequest(new { error = "Source ID is required." });
    }

    if (request.UpdateRateMs != DaRuntimeSettings.FixedUpdateRateMs)
    {
        return Results.BadRequest(new { error = "Default update rate is fixed at 1000 ms; use per-tag rates for other cadences." });
    }

    DaRuntimeSettingsSnapshot snapshot = settings.SetSourceUpdateRate(request.SourceId, request.UpdateRateMs);
    DaSourceRuntimeSettings? source = snapshot.GetSource(request.SourceId);
    if (source is null)
    {
        return Results.BadRequest(new { error = "Source not found." });
    }

    return Results.Json(new
    {
        version = snapshot.Version,
        sourceId = source.SourceId,
        updateRateMs = source.UpdateRateMs
    });
});
app.MapPost("/api/da/sources/io-mode", (DaSourceIoModeRequest request, DaRuntimeSettings settings) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceId))
    {
        return Results.BadRequest(new { error = "Source ID is required." });
    }

    if (string.IsNullOrWhiteSpace(request.IoMode))
    {
        return Results.BadRequest(new { error = "I/O mode is required." });
    }

    string normalizedMode = SourceConfigMigration.NormalizeIoMode(request.IoMode);
    if (!string.Equals(normalizedMode, request.IoMode, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "I/O mode must be AutoDetect, Sync or Async20." });
    }

    DaRuntimeSettingsSnapshot snapshot = settings.SetSourceIoMode(request.SourceId, normalizedMode);
    DaSourceRuntimeSettings? source = snapshot.GetSource(request.SourceId);
    if (source is null)
    {
        return Results.BadRequest(new { error = "Source not found." });
    }

    return Results.Json(new
    {
        version = snapshot.Version,
        sourceId = source.SourceId,
        ioMode = source.IoMode
    });
});
app.MapGet("/api/da/sources/groups", (string? sourceId, DaRuntimeSettings settings, MappingStore mappingStore) =>
{
    if (string.IsNullOrWhiteSpace(sourceId))
    {
        return Results.BadRequest(new { error = "Source ID is required." });
    }

    DaRuntimeSettingsSnapshot snapshot = settings.GetSnapshot();
    DaSourceRuntimeSettings? source = snapshot.GetSource(sourceId);
    if (source is null)
    {
        return Results.BadRequest(new { error = "Source not found." });
    }

    if (!string.Equals(source.SourceType, SourceTypes.OpcDa, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Source is not an OPC DA source." });
    }

    // Rate buckets = distinct effective poll rates of the source's mapped tags
    // (per-tag PollRateMs wins, else the source default) — the same derivation the
    // poller uses to create OPC DA groups.
    // Also include any explicit GroupIoModes rates so a newly added group without tags still appears.
    (IReadOnlyList<TagMapping> mappings, _) = mappingStore.GetSnapshot();
    int defaultRate = Math.Max(100, source.UpdateRateMs);
    HashSet<int> rates = new();
    foreach (TagMapping mapping in mappings)
    {
        if (mapping.Enabled
            && string.Equals(mapping.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
        {
            rates.Add(mapping.PollRateMs > 0 ? mapping.PollRateMs : defaultRate);
        }
    }

    foreach (DaGroupIoMode g in source.GroupIoModes)
    {
        rates.Add(g.Rate);
    }

    if (rates.Count == 0)
    {
        rates.Add(defaultRate);
    }

    Dictionary<string, DaGroupIoMode> byName = source.GroupIoModes.ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);
    // tag counts per rate for display (legacy PollRateMs routing)
    Dictionary<int, int> tagCounts = new();
    foreach (int r in rates) tagCounts[r] = 0;
    foreach (TagMapping mapping in mappings)
    {
        if (mapping.Enabled && string.Equals(mapping.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
        {
            int eff = mapping.PollRateMs > 0 ? mapping.PollRateMs : defaultRate;
            if (tagCounts.ContainsKey(eff)) tagCounts[eff]++;
        }
    }
    // tag counts per group name for named groups (DaGroup)
    Dictionary<string, int> tagCountsByGroup = new(StringComparer.OrdinalIgnoreCase);
    foreach (var g in source.GroupIoModes) tagCountsByGroup[g.Name] = 0;
    foreach (TagMapping mapping in mappings)
    {
        if (mapping.Enabled && string.Equals(mapping.SourceId, sourceId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(mapping.DaGroup))
        {
            if (tagCountsByGroup.ContainsKey(mapping.DaGroup!)) tagCountsByGroup[mapping.DaGroup!]++;
        }
    }

    // For named groups, return one entry per DaGroupIoMode (per Name), plus any distinct PollRateMs without explicit group
    var groupsByName = source.GroupIoModes.ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);
    var groups = new List<object>();
    // First, explicit named groups
    foreach (var g in source.GroupIoModes.OrderBy(x => x.Name))
    {
        int tc = tagCountsByGroup.TryGetValue(g.Name, out int c) ? c : 0;
        // For named groups, also count PollRateMs tags that match Rate but have no DaGroup (back-compat)
        if (tc == 0) tagCounts.TryGetValue(g.Rate, out tc);
        groups.Add(new
        {
            name = g.Name,
            rate = g.Rate,
            groupId = g.Name,
            ioMode = (string?)g.IoMode,
            effective = g.IoMode,
            isDefault = false,
            tagCount = tc
        });
    }
    // Then, distinct PollRateMs rates that have no explicit named group
    var existingNames = new HashSet<string>(source.GroupIoModes.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
    var distinctRates = new HashSet<int>(rates);
    foreach (int rate in distinctRates.OrderBy(r => r))
    {
        // if there's already a named group with this rate, skip (to avoid duplicate Rate entries when Name is the key)
        // Instead, check if any named group has this rate - if yes, don't create default
        bool hasNamedWithRate = source.GroupIoModes.Any(g => g.Rate == rate);
        if (hasNamedWithRate) continue;
        int tc = tagCounts.TryGetValue(rate, out int c) ? c : 0;
        groups.Add(new
        {
            name = $"OpcBridge_{rate}",
            rate,
            groupId = $"OpcBridge_{rate}",
            ioMode = (string?)null,
            effective = source.IoMode,
            isDefault = true,
            tagCount = tc
        });
    }
    // Ensure at least default if no groups at all
    if (groups.Count == 0)
    {
        int defRate = rates.FirstOrDefault();
        if (defRate == 0) defRate = defaultRate;
        groups.Add(new
        {
            name = $"OpcBridge_{defRate}",
            rate = defRate,
            groupId = $"OpcBridge_{defRate}",
            ioMode = (string?)null,
            effective = source.IoMode,
            isDefault = true,
            tagCount = tagCounts.TryGetValue(defRate, out int c) ? c : 0
        });
    }
    var groupsArray = groups.OrderBy(g => ((dynamic)g).name).ToArray();

    return Results.Json(new
    {
        version = snapshot.Version,
        sourceId = source.SourceId,
        sourceIoMode = source.IoMode,
        groups = groupsArray
    });
});
app.MapPost("/api/da/sources/groups", (DaGroupIoModeRequest request, DaRuntimeSettings settings, MappingStore mappingStore) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceId))
    {
        return Results.BadRequest(new { error = "Source ID is required." });
    }

    if (request.Rate < 100)
    {
        return Results.BadRequest(new { error = "Rate must be at least 100 ms." });
    }

    if (string.IsNullOrWhiteSpace(request.IoMode))
    {
        return Results.BadRequest(new { error = "I/O mode is required." });
    }

    string normalizedMode = SourceConfigMigration.NormalizeIoMode(request.IoMode);
    if (!string.Equals(normalizedMode, request.IoMode, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "I/O mode must be AutoDetect, Sync or Async20." });
    }

    if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Group name is required." });
    if (!string.IsNullOrWhiteSpace(request.RenameFrom) &&
        !string.Equals(request.RenameFrom, request.Name, StringComparison.OrdinalIgnoreCase))
    {
        // Rename: rewrite mapping references so faceplates follow the new name.
        mappingStore.RenameDaGroup(request.SourceId, request.RenameFrom!, request.Name);
    }
    DaRuntimeSettingsSnapshot snapshot = settings.SetSourceGroupIoMode(request.SourceId, request.Name!, request.Rate, normalizedMode);
    DaSourceRuntimeSettings? source = snapshot.GetSource(request.SourceId);
    if (source is null)
    {
        return Results.BadRequest(new { error = "Source not found." });
    }
    // Keep member tags' numeric rate aligned with the named group (COM buckets are rate-keyed).
    int tagsSynced = mappingStore.SyncDaGroupRate(request.SourceId, request.Name!, request.Rate);

    return Results.Json(new
    {
        version = snapshot.Version,
        sourceId = source.SourceId,
        rate = request.Rate,
        ioMode = normalizedMode,
        tagsSynced
    });
});
app.MapPost("/api/da/sources/groups/reset", (DaGroupIoModeResetRequest request, DaRuntimeSettings settings, MappingStore mappingStore) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceId))
    {
        return Results.BadRequest(new { error = "Source ID is required." });
    }

    DaRuntimeSettingsSnapshot snapshot = settings.ResetSourceGroupIoMode(request.SourceId, request.Name, request.Rate);
    DaSourceRuntimeSettings? source = snapshot.GetSource(request.SourceId);
    if (source is null)
    {
        return Results.BadRequest(new { error = "Source not found." });
    }
    // Group deleted: member tags fall back to Source Default (per design).
    int tagsDetached = string.IsNullOrWhiteSpace(request.Name)
        ? 0
        : mappingStore.ClearDaGroup(request.SourceId, request.Name!);

    return Results.Json(new
    {
        version = snapshot.Version,
        sourceId = source.SourceId,
        rate = request.Rate,
        tagsDetached
    });
});
app.MapPost("/api/da/sources", (DaServerConfigRequest request, DaRuntimeSettings settings, UaServerHost uaServer) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceId))
    {
        return Results.BadRequest(new { error = "Source ID is required." });
    }

    if (request.SourceId.Any(char.IsWhiteSpace))
    {
        return Results.BadRequest(new { error = "Source ID must not contain spaces." });
    }

    if (!TryValidateSourceUpsert(request, uaServer.GetOptions().EndpointUrl, settings, out string? validationError))
    {
        return Results.BadRequest(new { error = validationError });
    }

    string upsertType = request.SourceType ?? string.Empty;
    OpcDaSourceOptions? upsertDa = null;
    OpcUaSourceOptions? upsertUa = null;
    MelsecA3nSourceOptions? upsertMelsec = null;
    S7200PpiSourceOptions? upsertS7200 = null;
    if (string.Equals(upsertType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
    {
        upsertUa = new OpcUaSourceOptions(
            request.EndpointUrl ?? string.Empty,
            request.SecurityMode ?? string.Empty,
            request.SecurityPolicy ?? string.Empty,
            request.UaUsername,
            request.UaPassword,
            request.SessionTimeoutMs,
            request.ReconnectDelayMs,
            request.WatchdogTimeoutMs ?? 60000);
    }
    else if (string.Equals(upsertType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
    {
        upsertMelsec = new MelsecA3nSourceOptions(
            request.Transport ?? string.Empty,
            request.SerialPortName ?? string.Empty,
            request.BaudRate,
            request.DataBits,
            request.Parity ?? string.Empty,
            request.StopBits ?? string.Empty,
            request.StationNo ?? string.Empty,
            request.PcNo ?? string.Empty,
            request.TimeoutMs,
            request.RetryCount);
    }
    else if (string.Equals(upsertType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
    {
        upsertS7200 = new S7200PpiSourceOptions(
            request.Transport ?? "Serial",
            request.SerialPortName ?? string.Empty,
            request.BaudRate,
            request.DataBits,
            request.Parity ?? "Even",
            request.StopBits ?? "One",
            request.LocalPpiAddress,
            request.RemotePpiAddress,
            request.TimeoutMs,
            request.RetryCount);
    }
    else
    {
        upsertDa = new OpcDaSourceOptions(
            request.ProgId ?? string.Empty,
            request.Host ?? string.Empty,
            request.RemoteUsername,
            request.RemotePassword,
            request.RemoteDomain,
            ResolveGroupIoModes(request.Groups, settings, request.SourceId),
            ResolveWatchdogTimeoutMs(request.WatchdogTimeoutMs, settings, request.SourceId));
    }

    DaRuntimeSettingsSnapshot snapshot = settings.UpsertSource(new DaSourceRuntimeSettings(
        request.SourceId,
        request.DisplayName ?? string.Empty,
        upsertType,
        request.UpdateRateMs,
        request.UseSubscriptions ?? true,
        request.MaxMappedTags,
        upsertDa,
        upsertUa,
        upsertMelsec,
        upsertS7200,
        SourceConfigMigration.NormalizeIoMode(request.IoMode)));

    DaSourceRuntimeSettings source = snapshot.GetSource(request.SourceId)!;

    // Preserves existing per-group overrides when the request omits them (the
    // dashboard's source form does not carry group settings).
    static IReadOnlyList<DaGroupIoMode>? ResolveGroupIoModes(
        IReadOnlyList<DaGroupIoModeRequest>? groups,
        DaRuntimeSettings settings,
        string sourceId)
    {
        if (groups is not null)
        {
            return SourceConfigMigration.NormalizeGroupIoModes(
                groups.Select(g => new DaGroupIoMode(g.Name, g.Rate, g.IoMode)));
        }

        DaSourceRuntimeSettings? existing = settings.GetSnapshot().GetSource(sourceId);
        return existing?.OpcDa?.GroupIoModes;
    }

    // The dashboard's source form does not carry the watchdog timeout yet, so an omitted
    // value keeps whatever the source already had instead of resetting it.
    static int ResolveWatchdogTimeoutMs(
        int? requested,
        DaRuntimeSettings settings,
        string sourceId)
    {
        if (requested is not null)
        {
            return Math.Max(0, requested.Value);
        }

        DaSourceRuntimeSettings? existing = settings.GetSnapshot().GetSource(sourceId);
        return existing?.OpcDa?.WatchdogTimeoutMs ?? 60000;
    }

    return Results.Json(new
    {
        version = snapshot.Version,
        source = ToSourceApiDto(source)
    });
});
app.MapPost("/api/da/sources/remove", (DaSourceRemoveRequest request, DaRuntimeSettings settings, MappingStore store, InterlinkStore interlinkStore) =>
{
    if (!settings.TryRemoveSource(request.SourceId, out DaRuntimeSettingsSnapshot snapshot))
    {
        return Results.BadRequest(new { error = "Source was not found." });
    }

    long mappingVersion = store.RemoveSource(request.SourceId);
    long interlinkVersion = interlinkStore.RemoveBySource(request.SourceId);
    return Results.Json(new { version = snapshot.Version, mappingVersion, interlinkVersion });
});
app.MapPost("/api/drivers/melsec-a3n/parse-address", (MelsecParseAddressRequest request) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Address))
    {
        return Results.Json(new { ok = false, canonical = (string?)null, error = "Address is required." });
    }

    if (!MelsecAddressParser.TryParse(request.Address, out MelsecAddress address, out string error))
    {
        return Results.Json(new { ok = false, canonical = (string?)null, error });
    }

    return Results.Json(new { ok = true, canonical = address.Canonical, error = (string?)null });
});

app.MapPost("/api/drivers/melsec-a3n/test-connection", async (MelsecTestConnectionRequest request, DaRuntimeSettings settings) =>
{
    MelsecA3nClientOptions? options = ResolveMelsecTestOptions(request, settings);
    if (options is null)
    {
        return Results.Json(new { ok = false, error = "SerialPortName is required, or an existing MelsecA3n sourceId must be provided." });
    }

    try
    {
        await using MelsecA3nClient client = new(options);
        // Probe already enforces TimeoutMs/RetryCount; do not wrap with a second CTS
        // that races open+probe and surfaces "The operation was cancelled."
        await client.ConnectAsync(CancellationToken.None);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

// Accepted PLC device addresses for the MELSEC serial driver. The table is generated
// from the same catalog the parser enforces, so what this endpoint reports is exactly
// what tag upserts accept.
app.MapGet("/api/drivers/melsec-a3n/address-ranges", () =>
{
    return Results.Json(new
    {
        sourceType = SourceTypes.MelsecA3n,
        devices = MelsecDeviceCatalog.Devices.Select(range => new
        {
            device = range.Device,
            displayName = range.DisplayName,
            signalType = range.SignalType,
            numberBase = range.NumberBase.ToString(),
            min = range.MinNumber,
            max = range.MaxNumber,
            bitSuffixAllowed = range.BitSuffixAllowed,
            maxBitIndex = range.MaxBitIndex,
            aliases = range.Aliases,
            example = range.Example
        })
    });
});

app.MapPost("/api/drivers/s7200-ppi/parse-address", (S7200ParseAddressRequest request) =>
{
    if (!S7AddressParser.TryParse(request.Address, out S7Address address, out string error))
    {
        return Results.BadRequest(new { ok = false, error });
    }

    return Results.Json(new
    {
        ok = true,
        canonical = address.Canonical,
        area = address.Area.ToString(),
        byteOffset = address.ByteOffset,
        sizeBytes = address.SizeBytes,
        bitIndex = address.BitIndex
    });
});
app.MapPost("/api/drivers/s7200-ppi/test-connection", async (S7200TestConnectionRequest request, DaRuntimeSettings settings) =>
{
    S7200ClientOptions? options = ResolveS7200TestOptions(request, settings);
    if (options is null || string.IsNullOrWhiteSpace(options.SerialPortName))
    {
        return Results.Json(new { ok = false, error = "SerialPortName is required, or an existing S7200Ppi sourceId must be provided." });
    }

    try
    {
        await using S7200Client client = new(options);
        await client.ConnectAsync(CancellationToken.None);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});
app.MapGet("/api/serial/ports", () =>
{
    try
    {
        string[] ports = SerialPort.GetPortNames()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Results.Json(new { ports });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ports = Array.Empty<string>(), error = ex.Message });
    }
});
app.MapPost("/api/da/servers", async (DaServerBrowseRequest request) =>
{
    if (!OperatingSystem.IsWindows())
    {
        return Results.Json(new { error = "OPC DA enumeration requires Windows.", servers = Array.Empty<object>() });
    }

    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IReadOnlyList<OpcServerInfo> servers = await Task.Run(() => EnumerateDaServers(request.Host, request.Username, request.Password, request.Domain), cts.Token);
        return Results.Json(new { servers });
    }
    catch (OperationCanceledException)
    {
        return Results.Json(new { error = "Enumeration timed out. Check OpcEnum service and DCOM settings.", servers = Array.Empty<object>() });
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message, servers = Array.Empty<object>() });
    }
});
app.MapPost("/api/da/tags", async (DaTagBrowseRequest request, ILogger<Program> logger) =>
{
    if (!OperatingSystem.IsWindows())
    {
        return Results.Json(new { error = "OPC DA browsing requires Windows.", branches = Array.Empty<object>(), tags = Array.Empty<object>() });
    }

    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        OpcTagBrowseResult result = await Task.Run(() => BrowseDaTags(request), cts.Token);
        if (result.Warnings is { Count: > 0 })
        {
            // Warning, not Debug: the dashboard log store drops Debug for the Program category, and a
            // browse that came back empty *because* of a server quirk must be findable in the logs.
            logger.LogWarning(
                "OPC DA browse {ProgId}@{Host}: {Folders} folder(s), {Tags} tag(s), warnings: {Warnings}",
                request.ProgId, request.Host, result.Branches.Count, result.Tags.Count, result.Warnings);
        }

        return Results.Json(new
        {
            branches = result.Branches,
            tags = result.Tags.Select(tag => new
            {
                name = tag.Name,
                itemId = tag.ItemId,
                canonicalDataType = tag.CanonicalDataType,
                accessRights = tag.AccessRights
            }),
            warnings = result.Warnings ?? Array.Empty<string>()
        });
    }
    catch (OperationCanceledException)
    {
        logger.LogWarning("OPC DA browse {ProgId}@{Host} timed out after 15s.", request.ProgId, request.Host);
        return Results.Json(new { error = "Tag browse timed out. Check the server and DCOM settings.", branches = Array.Empty<object>(), tags = Array.Empty<object>() });
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "OPC DA browse {ProgId}@{Host} failed.", request.ProgId, request.Host);
        return Results.Json(new { error = exception.Message, branches = Array.Empty<object>(), tags = Array.Empty<object>() });
    }
});
app.MapGet("/api/interlinks", (InterlinkStore store) =>
{
    (IReadOnlyList<InterlinkRule> rules, long version) = store.GetSnapshot();
    return Results.Json(new
    {
        links = rules.Select(ToInterlinkDto),
        version
    });
});
app.MapPost("/api/interlinks", (CreateInterlinkRequest request, InterlinkStore store, MappingStore mappingStore, IInterlinkMetadataResolver metadataResolver) =>
{
    if (request.Link is null)
    {
        return Results.BadRequest(new { error = "Link is required." });
    }

    if (!TryBuildValidatedInterlinkRule(request.Link, null, mappingStore, store, metadataResolver, out InterlinkRule rule, out string? error))
    {
        return Results.BadRequest(new { error });
    }

    if (!store.TryAdd(rule, out long version, out string? storeError))
    {
        return string.Equals(storeError, "Rule already exists.", StringComparison.Ordinal)
            ? Results.Conflict(new { error = storeError })
            : Results.BadRequest(new { error = storeError });
    }

    return Results.Json(new { link = ToInterlinkDto(rule), version });
});

app.MapPut("/api/interlinks/{id:guid}", (Guid id, UpdateInterlinkRequest request, InterlinkStore store, MappingStore mappingStore, IInterlinkMetadataResolver metadataResolver) =>
{
    if (request.Link is null)
    {
        return Results.BadRequest(new { error = "Link is required." });
    }
    if (!InterlinkApiHelpers.TryGetStoredInterlinkRule(store, id, out _))
    {
        return Results.NotFound(new { error = "Rule not found." });
    }

    if (!TryBuildValidatedInterlinkRule(request.Link, id, mappingStore, store, metadataResolver, out InterlinkRule rule, out string? error))
    {
        return Results.BadRequest(new { error });
    }

    if (!store.TryUpdate(rule, out long version, out string? storeError))
    {
        return string.Equals(storeError, "Rule not found.", StringComparison.Ordinal)
            ? Results.NotFound(new { error = storeError })
            : Results.BadRequest(new { error = storeError });
    }

    return Results.Json(new { link = ToInterlinkDto(rule), version });
});
app.MapDelete("/api/interlinks/{id:guid}", (Guid id, InterlinkStore store) =>
{
    if (!store.TryRemove(id, out long version))
    {
        return Results.NotFound(new { error = "Link not found." });
    }

    return Results.Json(new { version });
});
app.MapGet("/api/mappings", (MappingStore store) =>
{
    (IReadOnlyList<TagMapping> mappings, long version) = store.GetSnapshot();
    return Results.Json(new { mappings, version });
});
app.MapPost("/api/mappings/add", (MappingAddRequest request, MappingStore store, DaRuntimeSettings settings) =>
{
    if (request.Tags is null || request.Tags.Count == 0)
    {
        return Results.BadRequest(new { error = "At least one mapping is required." });
    }

    if (request.Tags.Any(tag => string.IsNullOrWhiteSpace(tag.SourceId) || string.IsNullOrWhiteSpace(tag.ItemId)))
    {
        return Results.BadRequest(new { error = "Source ID and DA Item ID are required for every mapping." });
    }

    List<TagMapping> tags = request.Tags.Select(ToTagMapping).ToList();
    if (ValidateMelsecMappings(tags, settings, store, out string mappingError))
    {
        return Results.BadRequest(new { error = mappingError });
    }
    if (ValidateS7Mappings(tags, settings, store, out mappingError))
    {
        return Results.BadRequest(new { error = mappingError });
    }

    if (TryGetMaxMappedTagsError(tags, store, settings) is { } maxError)
    {
        return Results.BadRequest(new { error = maxError });
    }

    long version = store.Add(tags, out MappingAddResult addResult);
    return Results.Json(AddResultPayload(version, addResult));
});
app.MapPost("/api/mappings/bulk-add", (MappingAddRequest request, MappingStore store, DaRuntimeSettings settings) =>
{
    if (request.Tags is null || request.Tags.Count == 0)
    {
        return Results.BadRequest(new { error = "At least one mapping is required." });
    }

    List<TagMapping> tags = request.Tags
        .Select(tag =>
        {
            TagMapping mapping = ToTagMapping(tag);
            mapping.SourceId = string.IsNullOrWhiteSpace(tag.SourceId) ? "default" : tag.SourceId;
            return mapping;
        })
        .Where(tag => !string.IsNullOrWhiteSpace(tag.ItemId))
        .ToList();

    if (ValidateMelsecMappings(tags, settings, store, out string mappingError))
    {
        return Results.BadRequest(new { error = mappingError });
    }
    if (ValidateS7Mappings(tags, settings, store, out mappingError))
    {
        return Results.BadRequest(new { error = mappingError });
    }

    if (TryGetMaxMappedTagsError(tags, store, settings) is { } maxError)
    {
        return Results.BadRequest(new { error = maxError });
    }

    long version = store.Add(tags, out MappingAddResult addResult);
    return Results.Json(new
    {
        version,
        received = request.Tags.Count,
        added = addResult.Added,
        skippedExisting = addResult.SkippedExisting,
        existing = ExistingKeysPayload(addResult)
    });
});
app.MapPost("/api/mappings/update", (MappingUpdateRequest request, MappingStore store, DaRuntimeSettings daSettings) =>
{
    if (string.IsNullOrWhiteSpace(request.Tag.SourceId) || string.IsNullOrWhiteSpace(request.Tag.ItemId))
    {
        return Results.BadRequest(new { error = "Source ID and DA Item ID are required." });
    }

    TagMapping tag = ToTagMapping(request.Tag);

    DaSourceRuntimeSettings? source = daSettings.GetSnapshot().GetSource(tag.SourceId);
    if (source is not null && string.Equals(source.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
    {
        if (!MelsecAddressParser.TryParse(tag.ItemId, out MelsecAddress address, out string addrError))
        {
            return Results.BadRequest(new { error = $"Invalid Melsec address '{tag.ItemId}': {addrError}" });
        }
        tag.ItemId = address.Canonical;
    }

    if (!store.TryUpdate(tag, out long version))
    {
        return Results.NotFound(new { error = "Mapping not found." });
    }

    return Results.Json(new { version });
});
app.MapPost("/api/mappings/remove", (MappingRemoveRequest request, MappingStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceId) || string.IsNullOrWhiteSpace(request.ItemId))
    {
        return Results.BadRequest(new { error = "Source ID and DA Item ID are required." });
    }

    long version = store.Remove(request.SourceId, request.ItemId);
    return Results.Json(new { version });
});
app.MapPost("/api/mappings/import/preview", async (
    MappingImportPreviewRequest request,
    MappingStore store,
    DaRuntimeSettings settings,
    OpcUaBrowseService uaBrowse,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceId))
    {
        return Results.BadRequest(new { error = "Source ID is required." });
    }

    DaSourceRuntimeSettings? source = settings.GetSnapshot().GetSource(request.SourceId);
    if (source is null)
    {
        return Results.BadRequest(new { error = $"Source '{request.SourceId}' was not found." });
    }

    if (!TagImportFile.TryParseMxOpcTags(request.Text, out List<ImportedTag> imported, out string parseError))
    {
        return Results.BadRequest(new { error = parseError });
    }

    // The comparison baseline is the tag list the source really exposes, read the same way the
    // Tag Browser reads it. A source that cannot be enumerated — no DA COM on this host, server
    // down, a driver source with no tag list — still gets the mapped/description comparison, and
    // the dialog says the source check was skipped rather than guessing at "not on the source".
    SourceTagCheck check = await ReadSourceTagsAsync(source, uaBrowse, settings, cancellationToken).ConfigureAwait(false);

    Dictionary<string, TagMapping> mapped = new(StringComparer.OrdinalIgnoreCase);
    foreach (TagMapping tag in store.GetBySource(source.SourceId))
    {
        mapped[tag.ItemId] = tag;
    }

    List<TagImportRow> rows = TagImportComparer.Compare(imported, mapped, check.Tags);

    // The comparison runs both ways: every file row against the source, and the source's own
    // tags the file leaves out. The dialog shows the second half so the operator can see what
    // the server really exposes — and which of those tags the bridge already maps.
    TagImportReconciliation reconciliation = TagImportComparer.Reconcile(rows, mapped, check.Tags);

    return Results.Json(new
    {
        sourceId = source.SourceId,
        sourceName = source.DisplayName,
        sourceChecked = check.Tags is not null,
        sourceTruncated = check.Truncated,
        sourceError = check.Error,
        sourceTagCount = check.Tags?.Count ?? 0,
        mappedTags = mapped.Count,
        rowCount = imported.Count,
        rows = rows.Select(row => new
        {
            name = row.Name,
            itemId = row.ItemId,
            description = row.Description,
            group = row.Group,
            status = row.Status,
            existingDescription = row.ExistingDescription,
            addedUtc = row.AddedUtc,
            onSource = row.OnSource
        }),
        sourceOnlyCount = reconciliation.SourceOnlyCount,
        sourceOnlyTruncated = reconciliation.SourceOnlyTruncated,
        sourceOnly = reconciliation.SourceOnly.Select(tag => new
        {
            itemId = tag.ItemId,
            mapped = tag.Mapped,
            description = tag.Description
        })
    });
});

app.MapGet("/api/config/export", (DaRuntimeSettings daSettings, MappingStore mappingStore) =>
{
    DaRuntimeSettingsSnapshot daSnapshot = daSettings.GetSnapshot();
    (IReadOnlyList<TagMapping> mappings, _) = mappingStore.GetSnapshot();

    return Results.Json(new
    {
        exportedAtUtc = DateTime.UtcNow,
        daSources = new
        {
            updateRateMs = daSnapshot.UpdateRateMs,
            useSubscriptions = daSnapshot.UseSubscriptions,
            sources = daSnapshot.Sources.Select(ToSourceApiDto)
        },
        mappings = mappings
    });
});

app.MapPost("/api/config/import", async (HttpContext context, DaRuntimeSettings daSettings, MappingStore mappingStore, ILogger<Program> logger) =>
{
    try
    {
        using JsonDocument doc = await JsonDocument.ParseAsync(context.Request.Body);
        JsonElement root = doc.RootElement;

        // Restore DA sources
        if (root.TryGetProperty("daSources", out JsonElement daSourcesEl))
        {
            // Fixed policy: import always lands at the fixed 1 s source default rate.
            int updateRate = DaRuntimeSettings.FixedUpdateRateMs;
            List<DaSourceRuntimeSettings> sources = new();

            if (daSourcesEl.TryGetProperty("sources", out JsonElement sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement s in sourcesEl.EnumerateArray())
                {
                    string rawSourceType = s.TryGetProperty("sourceType", out JsonElement rawTypeEl) ? rawTypeEl.GetString() ?? string.Empty : string.Empty;
                    if (SourceConfigMigration.IsRetiredSourceType(rawSourceType))
                    {
                        logger.LogWarning(
                            "Imported source '{SourceId}' uses the removed '{SourceType}' source type and was skipped.",
                            s.TryGetProperty("sourceId", out JsonElement skippedId) ? skippedId.GetString() : null,
                            rawSourceType);
                        continue;
                    }

                    sources.Add(SourceConfigMigration.FromDto(new SourceConfigDto
                    {
                        SourceId = s.TryGetProperty("sourceId", out JsonElement sid) ? sid.GetString() : "default",
                        DisplayName = s.TryGetProperty("displayName", out JsonElement dn) ? dn.GetString() : string.Empty,
                        SourceType = s.TryGetProperty("sourceType", out JsonElement st) ? st.GetString() : string.Empty,
                        ProgId = s.TryGetProperty("progId", out JsonElement pid) ? pid.GetString() : string.Empty,
                        Host = s.TryGetProperty("host", out JsonElement h) ? h.GetString() ?? "localhost" : "localhost",
                        RemoteUsername = s.TryGetProperty("remoteUsername", out JsonElement ru) ? ru.GetString() : null,
                        RemotePassword = null, // password not exported — must be re-entered on import
                        RemoteDomain = s.TryGetProperty("remoteDomain", out JsonElement rd) ? rd.GetString() : null,
                        Transport = s.TryGetProperty("transport", out JsonElement tr) ? tr.GetString() : string.Empty,
                        SerialPortName = s.TryGetProperty("serialPortName", out JsonElement spn) ? spn.GetString() : string.Empty,
                        BaudRate = s.TryGetProperty("baudRate", out JsonElement br) ? br.GetInt32() : 0,
                        DataBits = s.TryGetProperty("dataBits", out JsonElement dbits) ? dbits.GetInt32() : 0,
                        Parity = s.TryGetProperty("parity", out JsonElement par) ? par.GetString() : string.Empty,
                        StopBits = s.TryGetProperty("stopBits", out JsonElement sb) ? sb.GetString() : string.Empty,
                        StationNo = s.TryGetProperty("stationNo", out JsonElement sn) ? sn.GetString() : string.Empty,
                        PcNo = s.TryGetProperty("pcNo", out JsonElement pn) ? pn.GetString() : string.Empty,
                        TimeoutMs = s.TryGetProperty("timeoutMs", out JsonElement to) ? to.GetInt32() : 0,
                        RetryCount = s.TryGetProperty("retryCount", out JsonElement rc) ? rc.GetInt32() : -1,
                        EndpointUrl = s.TryGetProperty("endpointUrl", out JsonElement eu) ? eu.GetString() : string.Empty,
                        SecurityMode = s.TryGetProperty("securityMode", out JsonElement sm) ? sm.GetString() : string.Empty,
                        SecurityPolicy = s.TryGetProperty("securityPolicy", out JsonElement sp) ? sp.GetString() : string.Empty,
                        UaUsername = s.TryGetProperty("uaUsername", out JsonElement uu) ? uu.GetString() : null,
                        UaPassword = null, // UA password not exported
                        SessionTimeoutMs = s.TryGetProperty("sessionTimeoutMs", out JsonElement sto) ? sto.GetInt32() : 0,
                        ReconnectDelayMs = s.TryGetProperty("reconnectDelayMs", out JsonElement rcd) ? rcd.GetInt32() : 0,
                        MaxMappedTags = s.TryGetProperty("maxMappedTags", out JsonElement mmt) ? mmt.GetInt32() : 0,
                        UseSubscriptions = s.TryGetProperty("useSubscriptions", out JsonElement usrc) ? usrc.GetBoolean() : true,
                        UpdateRateMs = DaRuntimeSettings.FixedUpdateRateMs,
                        // Nested export shape (if present)
                        OpcDa = s.TryGetProperty("opcDa", out JsonElement opcDaEl) && opcDaEl.ValueKind == JsonValueKind.Object
                            ? new OpcDaSourceOptionsDto
                            {
                                ProgId = opcDaEl.TryGetProperty("progId", out JsonElement opid) ? opid.GetString() : null,
                                Host = opcDaEl.TryGetProperty("host", out JsonElement oh) ? oh.GetString() : null,
                                RemoteUsername = opcDaEl.TryGetProperty("remoteUsername", out JsonElement oru) ? oru.GetString() : null,
                                RemoteDomain = opcDaEl.TryGetProperty("remoteDomain", out JsonElement ord) ? ord.GetString() : null
                            }
                            : null,
                        OpcUa = s.TryGetProperty("opcUa", out JsonElement opcUaEl) && opcUaEl.ValueKind == JsonValueKind.Object
                            ? new OpcUaSourceOptionsDto
                            {
                                EndpointUrl = opcUaEl.TryGetProperty("endpointUrl", out JsonElement oeu) ? oeu.GetString() : null,
                                SecurityMode = opcUaEl.TryGetProperty("securityMode", out JsonElement osm) ? osm.GetString() : null,
                                SecurityPolicy = opcUaEl.TryGetProperty("securityPolicy", out JsonElement osp) ? osp.GetString() : null,
                                Username = opcUaEl.TryGetProperty("username", out JsonElement oun) ? oun.GetString() : null,
                                UaUsername = opcUaEl.TryGetProperty("uaUsername", out JsonElement ouu) ? ouu.GetString() : null,
                                SessionTimeoutMs = opcUaEl.TryGetProperty("sessionTimeoutMs", out JsonElement osto) ? osto.GetInt32() : 0,
                                ReconnectDelayMs = opcUaEl.TryGetProperty("reconnectDelayMs", out JsonElement orcd) ? orcd.GetInt32() : 0,
                                MaxMappedTags = opcUaEl.TryGetProperty("maxMappedTags", out JsonElement ommt) ? ommt.GetInt32() : 0
                            }
                            : null,
                        Melsec = s.TryGetProperty("melsec", out JsonElement melEl) && melEl.ValueKind == JsonValueKind.Object
                            ? new MelsecA3nSourceOptionsDto
                            {
                                Transport = melEl.TryGetProperty("transport", out JsonElement mtr) ? mtr.GetString() : null,
                                SerialPortName = melEl.TryGetProperty("serialPortName", out JsonElement msp) ? msp.GetString() : null,
                                BaudRate = melEl.TryGetProperty("baudRate", out JsonElement mbr) ? mbr.GetInt32() : 0,
                                DataBits = melEl.TryGetProperty("dataBits", out JsonElement mdb) ? mdb.GetInt32() : 0,
                                Parity = melEl.TryGetProperty("parity", out JsonElement mpa) ? mpa.GetString() : null,
                                StopBits = melEl.TryGetProperty("stopBits", out JsonElement msb) ? msb.GetString() : null,
                                StationNo = melEl.TryGetProperty("stationNo", out JsonElement msn) ? msn.GetString() : null,
                                PcNo = melEl.TryGetProperty("pcNo", out JsonElement mpc) ? mpc.GetString() : null,
                                TimeoutMs = melEl.TryGetProperty("timeoutMs", out JsonElement mto) ? mto.GetInt32() : 0,
                                RetryCount = melEl.TryGetProperty("retryCount", out JsonElement mrc) ? mrc.GetInt32() : -1
                            }
                            : null
                    }, updateRate));
                }
            }

            bool useSubs = daSourcesEl.TryGetProperty("useSubscriptions", out JsonElement usEl) && usEl.GetBoolean();
            daSettings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(updateRate, useSubs, sources, 0));
        }

        // Restore mappings
        if (root.TryGetProperty("mappings", out JsonElement mappingsEl) && mappingsEl.ValueKind == JsonValueKind.Array)
        {
            List<TagMapping> tags = new();
            foreach (JsonElement m in mappingsEl.EnumerateArray())
            {
                tags.Add(new TagMapping
                {
                    SourceId = m.TryGetProperty("sourceId", out JsonElement sid) ? sid.GetString() ?? "default" : "default",
                    ItemId = m.TryGetProperty("daItemId", out JsonElement di) ? di.GetString() ?? string.Empty
                        : m.TryGetProperty("itemId", out di) ? di.GetString() ?? string.Empty : string.Empty,
                    DisplayName = m.TryGetProperty("displayName", out JsonElement dn) ? dn.GetString() ?? string.Empty : string.Empty,
                    DataType = m.TryGetProperty("dataType", out JsonElement dt) ? dt.GetString() ?? "Auto" : "Auto",
                    UaNodeId = m.TryGetProperty("uaNodeId", out JsonElement un) ? un.GetString() ?? string.Empty : string.Empty,
                    Enabled = m.TryGetProperty("enabled", out JsonElement en) ? en.GetBoolean() : true,
                    Mode = m.TryGetProperty("mode", out JsonElement mo) ? mo.GetString() ?? "Source" : "Source",
                    ManualValue = m.TryGetProperty("manualValue", out JsonElement mv) ? mv.GetString() : null,
                    PollRateMs = m.TryGetProperty("pollRateMs", out JsonElement pr) ? pr.GetInt32() : 0,
                    DeadbandPct = m.TryGetProperty("deadbandPct", out JsonElement db) ? (float)db.GetDouble() : 0f,
                    Writeable = m.TryGetProperty("writeable", out JsonElement wr) ? wr.GetBoolean() : false,
                    Digital = m.TryGetProperty("digital", out JsonElement dg)
                        && dg.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? dg.GetBoolean() : null,
                    OnText = m.TryGetProperty("onText", out JsonElement ot) ? ot.GetString() : null,
                    OffText = m.TryGetProperty("offText", out JsonElement oft) ? oft.GetString() : null
                });
            }
            mappingStore.SetAll(tags);
        }

        return Results.Json(new { status = "ok", message = "Configuration imported. Sources and mappings restored. Note: DCOM passwords must be re-entered." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/ua/certificates", () =>
{
    string pkiRoot = Path.Combine(DataDirectory.Value, "pki");
    string trustedDir = Path.Combine(pkiRoot, "trusted");
    string rejectedDir = Path.Combine(pkiRoot, "rejected");

    List<object> ListCerts(string dir)
    {
        List<object> result = new();
        if (!Directory.Exists(dir)) return result;
        foreach (string file in Directory.GetFiles(dir, "*.der"))
        {
            string name = Path.GetFileName(file);
            FileInfo fi = new(file);
            result.Add(new { fileName = name, sizeBytes = fi.Length, lastModifiedUtc = fi.LastWriteTimeUtc });
        }
        return result;
    }

    return Results.Json(new
    {
        trusted = ListCerts(trustedDir),
        rejected = ListCerts(rejectedDir)
    });
});

app.MapPost("/api/ua/certificates/approve", (HttpContext context) =>
{
    string body = new StreamReader(context.Request.Body).ReadToEnd();
    string? fileName = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("fileName").GetString();
    if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
    {
        return Results.BadRequest(new { error = "Invalid file name." });
    }

    string rejectedPath = Path.Combine(DataDirectory.Value, "pki", "rejected", fileName);
    string trustedPath = Path.Combine(DataDirectory.Value, "pki", "trusted", fileName);

    if (!File.Exists(rejectedPath))
    {
        return Results.NotFound(new { error = $"Certificate '{fileName}' not found in rejected folder." });
    }

    Directory.CreateDirectory(Path.GetDirectoryName(trustedPath)!);
    File.Move(rejectedPath, trustedPath, overwrite: true);
    return Results.Json(new { status = "ok", message = $"Certificate '{fileName}' approved and moved to trusted." });
});

app.MapPost("/api/ua/certificates/reject", (HttpContext context) =>
{
    string body = new StreamReader(context.Request.Body).ReadToEnd();
    string? fileName = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("fileName").GetString();
    if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
    {
        return Results.BadRequest(new { error = "Invalid file name." });
    }

    string trustedPath = Path.Combine(DataDirectory.Value, "pki", "trusted", fileName);
    string rejectedPath = Path.Combine(DataDirectory.Value, "pki", "rejected", fileName);

    if (!File.Exists(trustedPath))
    {
        return Results.NotFound(new { error = $"Certificate '{fileName}' not found in trusted folder." });
    }

    Directory.CreateDirectory(Path.GetDirectoryName(rejectedPath)!);
    File.Move(trustedPath, rejectedPath, overwrite: true);
    return Results.Json(new { status = "ok", message = $"Certificate '{fileName}' rejected and moved to rejected." });
});

app.MapPost("/api/ua/certificates/delete", (HttpContext context) =>
{
    string body = new StreamReader(context.Request.Body).ReadToEnd();
    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
    string? fileName = doc.RootElement.GetProperty("fileName").GetString();
    string? folder = doc.RootElement.GetProperty("folder").GetString();

    if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
    {
        return Results.BadRequest(new { error = "Invalid file name." });
    }

    if (folder != "trusted" && folder != "rejected")
    {
        return Results.BadRequest(new { error = "Folder must be 'trusted' or 'rejected'." });
    }

    string path = Path.Combine(DataDirectory.Value, "pki", folder, fileName);
    if (!File.Exists(path))
    {
        return Results.NotFound(new { error = $"Certificate '{fileName}' not found in {folder}." });
    }

    File.Delete(path);
    return Results.Json(new { status = "ok", message = $"Certificate '{fileName}' deleted from {folder}." });
});

app.MapGet("/api/ua/settings", (UaServerHost uaServer) =>
{
    UaServerOptions opts = uaServer.GetOptions();
    return Results.Json(new
    {
        endpointUrl = opts.EndpointUrl,
        autoAcceptUntrustedCertificates = opts.AutoAcceptUntrustedCertificates,
        requireAuthentication = opts.RequireAuthentication,
        username = opts.Username ?? string.Empty,
        allowedIpAddresses = opts.AllowedIpAddresses ?? new List<string>()
    });
});

app.MapPost("/api/ua/settings", async (HttpContext context, UaServerHost uaServer) =>
{
    try
    {
        using System.Text.Json.JsonDocument doc = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
        System.Text.Json.JsonElement root = doc.RootElement;

        UaServerOptions current = uaServer.GetOptions();

        // Issue #14: enabling the credential gate with no usable credentials — in the
        // request or already stored — would lock every client out of the server, so
        // refuse that state instead of persisting it.
        if (root.TryGetProperty("requireAuthentication", out var raReq) && raReq.GetBoolean())
        {
            string effectiveUsername = FirstNonBlank(
                root.TryGetProperty("username", out var unReq) ? unReq.GetString() : null,
                current.Username);
            string effectivePassword = FirstNonBlank(
                root.TryGetProperty("password", out var pwReq) ? pwReq.GetString() : null,
                current.Password);
            if (string.IsNullOrWhiteSpace(effectiveUsername) || string.IsNullOrWhiteSpace(effectivePassword))
            {
                return Results.BadRequest(new
                {
                    error = "Enabling credentials requires a username and password (in the request or already stored)."
                });
            }
        }

        UaServerOptions updated = new()
        {
            ApplicationName = current.ApplicationName,
            EndpointUrl = root.TryGetProperty("endpointUrl", out var ep) ? ep.GetString() ?? current.EndpointUrl : current.EndpointUrl,
            AutoAcceptUntrustedCertificates = root.TryGetProperty("autoAcceptUntrustedCertificates", out var aa) ? aa.GetBoolean() : current.AutoAcceptUntrustedCertificates,
            RequireAuthentication = root.TryGetProperty("requireAuthentication", out var ra) ? ra.GetBoolean() : current.RequireAuthentication,
            Username = root.TryGetProperty("username", out var un) && !string.IsNullOrEmpty(un.GetString()) ? un.GetString() : current.Username,
            Password = root.TryGetProperty("password", out var pw) && !string.IsNullOrEmpty(pw.GetString()) ? pw.GetString() : current.Password,
            AllowedIpAddresses = root.TryGetProperty("allowedIpAddresses", out var ip) && ip.ValueKind == System.Text.Json.JsonValueKind.Array
                ? ip.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
                : current.AllowedIpAddresses
        };

        uaServer.UpdateOptions(updated);
        return Results.Json(new { status = "ok", message = "UA settings saved. Restart the bridge to apply (endpoint/auth changes take effect on restart)." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/ua/test-connection", async (
    UaTestConnectionRequest request,
    OpcUaBrowseService browseService,
    DaRuntimeSettings settings,
    CancellationToken cancellationToken) =>
{
    if (!TryResolveUaConnection(
            request.SourceId,
            request.EndpointUrl,
            request.SecurityMode,
            request.SecurityPolicy,
            request.Username,
            request.Password,
            settings,
            out OpcUaSourceClientOptions? options,
            out string? resolveError))
    {
        return Results.BadRequest(new { error = resolveError, ok = false });
    }

    UaTestConnectionResult result = await browseService
        .TestConnectionAsync(options!, cancellationToken)
        .ConfigureAwait(false);

    if (!result.Ok)
    {
        return Results.Json(new { ok = false, error = result.Error ?? "Connection failed." });
    }

    return Results.Json(new
    {
        ok = true,
        serverProductName = result.ServerProductName,
        sessionId = result.SessionId
    });
});
app.MapPost("/api/ua/discover", async (
    UaDiscoverRequest request,
    OpcUaBrowseService browseService,
    DaRuntimeSettings settings,
    CancellationToken cancellationToken) =>
{
    if (!TryResolveUaConnection(
            request.SourceId,
            request.EndpointUrl,
            request.SecurityMode,
            request.SecurityPolicy,
            request.Username,
            request.Password,
            settings,
            out OpcUaSourceClientOptions? options,
            out string? resolveError))
    {
        return Results.BadRequest(new { error = resolveError, ok = false });
    }

    UaDiscoverResult result = await browseService
        .DiscoverServersAsync(options!, cancellationToken)
        .ConfigureAwait(false);

    if (result.Error is not null)
    {
        return Results.Json(new { ok = false, error = result.Error });
    }

    return Results.Json(new
    {
        ok = true,
        servers = result.Servers.Select(s => new
        {
            serverUri = s.ServerUri,
            recordId = s.RecordId,
            discoveryUrl = s.DiscoveryUrl,
            serverName = s.ServerName,
            serverCapabilities = s.ServerCapabilities,
            isOnline = s.IsOnline
        }).ToList()
    });
});

app.MapPost("/api/ua/browse", async (
    UaBrowseRequest request,
    OpcUaBrowseService browseService,
    DaRuntimeSettings settings,
    CancellationToken cancellationToken) =>
{
    if (!TryResolveUaConnection(
            request.SourceId,
            request.EndpointUrl,
            request.SecurityMode,
            request.SecurityPolicy,
            request.Username,
            request.Password,
            settings,
            out OpcUaSourceClientOptions? options,
            out string? resolveError))
    {
        return Results.BadRequest(new { error = resolveError });
    }

    UaBrowseResult result = await browseService
        .BrowseAsync(
            options!,
            request.NodeId,
            request.MaxNodes ?? OpcUaBrowseService.DefaultMaxNodes,
            cancellationToken)
        .ConfigureAwait(false);

    if (result.Error is not null
        && result.Error.StartsWith("Invalid nodeId", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = result.Error, nodes = Array.Empty<object>() });
    }

    return Results.Json(new
    {
        nodes = result.Nodes.Select(n => new
        {
            nodeId = n.NodeId,
            displayName = n.DisplayName,
            nodeClass = n.NodeClass,
            hasChildren = n.HasChildren
        }),
        continuationPoint = result.ContinuationPoint,
        error = result.Error
    });
});

app.MapGet("/api/ua/subscriptions", (DaRuntimeSettings settings, BridgeWorker worker, string? sourceId) =>
{
    DaRuntimeSettingsSnapshot snapshot = settings.GetSnapshot();
    IReadOnlyDictionary<string, IReadOnlyList<UaSubscriptionStatus>> live = worker.GetUaSubscriptionStatus();
    IEnumerable<DaSourceRuntimeSettings> sources = string.IsNullOrWhiteSpace(sourceId)
        ? snapshot.Sources
        : snapshot.Sources.Where(s => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

    object payload = new
    {
        sources = sources
            .Where(s => string.Equals(s.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
            .Select(s =>
            {
                IReadOnlyList<UaSubscriptionStatus>? liveForSource = live.TryGetValue(s.SourceId, out IReadOnlyList<UaSubscriptionStatus>? list)
                    ? list
                    : null;

                // Live stats of the implicit default bucket (client reports it under the "" key
                // whenever unassigned tags are being monitored). Zeroed when not connected.
                UaSubscriptionStatus? defaultStatus = liveForSource?
                    .FirstOrDefault(st => st.BucketKey.Length == 0);

                return new
                {
                    sourceId = s.SourceId,
                    displayName = s.DisplayName,
                    defaultUpdateRateMs = s.UpdateRateMs,
                    defaultStats = new
                    {
                        updateRateMs = s.UpdateRateMs,
                        itemCount = defaultStatus?.ItemCount ?? 0,
                        actualPublishingIntervalMs = defaultStatus?.ActualPublishingIntervalMs ?? 0,
                        created = defaultStatus?.Created ?? false
                    },
                    subscriptions = s.UaSubscriptions
                        .OrderBy(def => def.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(def =>
                        {
                            UaSubscriptionStatus? status = liveForSource?
                                .FirstOrDefault(st => string.Equals(st.BucketKey, def.Name, StringComparison.OrdinalIgnoreCase));
                            return new
                            {
                                name = def.Name,
                                updateRateMs = def.UpdateRateMs,
                                itemCount = status?.ItemCount ?? 0,
                                actualPublishingIntervalMs = status?.ActualPublishingIntervalMs ?? 0,
                                created = status?.Created ?? false
                            };
                        })
                        .ToList()
                };
            })
            .ToList()
    };
    return Results.Json(payload);
});

app.MapPost("/api/ua/subscriptions", (UaSubscriptionUpsertRequest request, DaRuntimeSettings settings) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceId))
    {
        return Results.BadRequest(new { error = "sourceId is required." });
    }

    try
    {
        DaRuntimeSettingsSnapshot snapshot = settings.UpsertUaSubscription(request.SourceId, request.Name, request.UpdateRateMs);
        return Results.Ok(new { ok = true, version = snapshot.Version });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/ua/subscriptions/remove", (UaSubscriptionRemoveRequest request, DaRuntimeSettings settings, MappingStore store) =>
{
    try
    {
        DaRuntimeSettingsSnapshot snapshot = settings.RemoveUaSubscription(request.SourceId, request.Name);
        int movedMappings = store.ReassignSubscription(request.SourceId, request.Name);
        return Results.Ok(new { ok = true, version = snapshot.Version, movedMappings });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/mqtt/config", (MqttRuntimeSettings settings) =>
{
    MqttRuntimeSnapshot snapshot = settings.GetSnapshot();
    return Results.Json(new
    {
        enabled = snapshot.Options.Enabled,
        brokerUrl = snapshot.Options.BrokerUrl,
        clientId = snapshot.Options.ClientId,
        userName = snapshot.Options.UserName,
        password = snapshot.Options.Password,
        tls = snapshot.Options.Tls,
        ignoreCertErrors = snapshot.Options.IgnoreCertErrors,
        topicPrefix = snapshot.Options.TopicPrefix,
        payloadFields = snapshot.Options.PayloadFields.ToString()
    });
});
app.MapPost("/api/mqtt/config", (MqttConfigRequest request, MqttRuntimeSettings settings) =>
{
    MqttBrokerOptions options = settings.GetOptions();
    MqttBrokerOptions updated = new()
    {
        Enabled = request.Enabled,
        BrokerUrl = string.IsNullOrWhiteSpace(request.BrokerUrl) ? options.BrokerUrl : request.BrokerUrl.Trim(),
        ClientId = string.IsNullOrWhiteSpace(request.ClientId) ? options.ClientId : request.ClientId.Trim(),
        UserName = request.UserName,
        Password = request.Password,
        Tls = request.Tls,
        IgnoreCertErrors = request.IgnoreCertErrors,
        TopicPrefix = string.IsNullOrWhiteSpace(request.TopicPrefix) ? options.TopicPrefix : request.TopicPrefix.Trim(),
        PayloadFields = ParsePayloadFields(request.PayloadFields) ?? options.PayloadFields
    };
    settings.UpsertOptions(updated);
    return Results.Json(new { status = "ok" });
});
app.MapPost("/api/mqtt/connect", async (MqttRuntimeSettings settings, IMqttBridge bridge) =>
{
    try
    {
        await bridge.ConnectAsync(settings.GetOptions(), CancellationToken.None);
        return Results.Json(new { status = "ok", state = settings.GetSnapshot().State });
    }
    catch (Exception ex)
    {
        settings.SetState("Faulted", ex.Message);
        return Results.Json(new { status = "error", error = ex.Message });
    }
});
app.MapPost("/api/mqtt/disconnect", async (MqttRuntimeSettings settings, IMqttBridge bridge) =>
{
    await bridge.DisconnectAsync(CancellationToken.None);
    settings.SetState("Disconnected");
    return Results.Json(new { status = "ok" });
});
app.MapGet("/api/mqtt/status", (MqttRuntimeSettings settings) =>
{
    MqttRuntimeSnapshot snapshot = settings.GetSnapshot();
    return Results.Json(new
    {
        state = snapshot.State,
        lastError = snapshot.LastError,
        publishedCount = snapshot.PublishedCount,
        receivedCount = snapshot.ReceivedCount,
        publishedRate = snapshot.PublishedRate,
        receivedRate = snapshot.ReceivedRate,
        enabled = snapshot.Options.Enabled
    });
});
app.MapGet("/api/mqtt/values", (MqttValueStore values, string? direction, string? topic, int? page, int? pageSize) =>
{
    MqttValuePage page_ = values.GetEntries(direction, topic, page ?? 1, pageSize ?? 50);
    return Results.Json(new
    {
        items = page_.Items.Select(e => new
        {
            direction = e.Direction,
            topic = e.Topic,
            value = e.Value,
            timestampUtc = e.TimestampUtc
        }),
        total = page_.Total
    });
});
app.MapGet("/api/influx/config", (InfluxRuntimeSettings settings) =>
{
    InfluxRuntimeSnapshot snapshot = settings.GetSnapshot();
    return Results.Json(new
    {
        enabled = snapshot.Options.Enabled,
        url = snapshot.Options.Url,
        org = snapshot.Options.Org,
        bucket = snapshot.Options.Bucket,
        token = snapshot.Options.Token,
        measurement = snapshot.Options.Measurement,
        timeoutMs = snapshot.Options.TimeoutMs,
        verifySsl = snapshot.Options.VerifySsl
    });
});
app.MapPost("/api/influx/probe", async (InfluxProbeRequest request, CancellationToken ct) =>
{
    string host = request.Host?.Trim() ?? string.Empty;
    if (host.Length == 0)
    {
        return Results.BadRequest(new { ok = false, error = "Host is required." });
    }

    // Normalize: strip scheme if pasted, keep optional port, else default 8086.
    string bare = host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        ? host[(host.IndexOf("://", StringComparison.Ordinal) + 3)..]
        : host;
    bare = bare.TrimEnd('/');
    string port = bare.Contains(':') ? string.Empty : ":8086";
    string baseUrl = "http://" + bare + port;

    using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        using HttpResponseMessage resp = await http.GetAsync(baseUrl + "/ping", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return Results.Json(new { ok = false, error = $"{baseUrl} responded HTTP {(int)resp.StatusCode} — not an InfluxDB API?" });
        }
        return Results.Json(new
        {
            ok = true,
            url = baseUrl,
            version = resp.Headers.Contains("X-Influxdb-Version")
                ? string.Join(",", resp.Headers.GetValues("X-Influxdb-Version"))
                : null
        });
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return Results.Json(new { ok = false, error = $"Cannot reach {baseUrl}: {ex.Message}" });
    }
});
app.MapPost("/api/influx/config", (InfluxConfigRequest request, InfluxRuntimeSettings settings) =>
{
    InfluxOptions options = settings.GetOptions();
    InfluxOptions updated = new()
    {
        Enabled = request.Enabled,
        Url = string.IsNullOrWhiteSpace(request.Url) ? options.Url : request.Url.Trim(),
        Org = string.IsNullOrWhiteSpace(request.Org) ? options.Org : request.Org.Trim(),
        Bucket = string.IsNullOrWhiteSpace(request.Bucket) ? options.Bucket : request.Bucket.Trim(),
        Token = request.Token,
        Measurement = string.IsNullOrWhiteSpace(request.Measurement) ? options.Measurement : request.Measurement.Trim(),
        TimeoutMs = request.TimeoutMs is null or <= 0 ? options.TimeoutMs : request.TimeoutMs.Value,
        VerifySsl = request.VerifySsl
    };
    settings.UpsertOptions(updated);
    return Results.Json(new { status = "ok" });
});
app.MapPost("/api/influx/connect", async (InfluxRuntimeSettings settings, IInfluxWriter writer) =>
{
    try
    {
        settings.SetState("Connecting");
        await writer.ConnectAsync(settings.GetOptions(), CancellationToken.None);
        return Results.Json(new { status = "ok", state = settings.GetSnapshot().State });
    }
    catch (Exception ex)
    {
        settings.SetState("Faulted", ex.Message);
        return Results.Json(new { status = "error", error = ex.Message });
    }
});
app.MapPost("/api/influx/disconnect", async (InfluxRuntimeSettings settings, IInfluxWriter writer) =>
{
    await writer.DisconnectAsync(CancellationToken.None);
    settings.SetState("Disconnected");
    return Results.Json(new { status = "ok" });
});
app.MapGet("/api/influx/status", (InfluxRuntimeSettings settings) =>
{
    InfluxRuntimeSnapshot snapshot = settings.GetSnapshot();
    return Results.Json(new
    {
        state = snapshot.State,
        lastError = snapshot.LastError,
        writtenCount = snapshot.WrittenCount,
        writtenRate = snapshot.WrittenRate,
        enabled = snapshot.Options.Enabled
    });
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAuth();
app.MapHub<HmiHub>("/hmi");

try
{
    await app.RunAsync().ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    // WindowsServiceLifetime.StopAsync raises this when the host's stop window
    // (HostOptions.ShutdownTimeout) elapses while a source is still being torn down — a slow
    // OPC shutdown must not be recorded as a crash. The exit is the intended one; note it on
    // the durable log (not the logging pipeline, which may be the thing that is stuck) and leave.
    try
    {
        new FileLogStore().Append(
            "[INFO] Host shutdown did not finish within HostOptions.ShutdownTimeout; exiting.");
    }
    catch
    {
    }
}

static IReadOnlyList<OpcServerInfo> EnumerateDaServers(string? host, string? username, string? password, string? domain)
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("OPC DA enumeration requires Windows.");
    }

    return OpcServerEnumerator.Enumerate(host, username, password, domain);
}

static OpcTagBrowseResult BrowseDaTags(DaTagBrowseRequest request)
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("OPC DA browsing requires Windows.");
    }

    return OpcTagBrowser.Browse(
        request.ProgId,
        request.Host,
        request.Path ?? string.Empty,
        request.Recursive,
        request.RemoteUsername,
        request.RemotePassword,
        request.RemoteDomain);
}

/// <summary>
/// The tag list a source really exposes, for the Maps import dialog's comparison. An OPC DA
/// server is read with the recursive browse the Tag Browser's "Browse All Tags" uses; an OPC UA
/// server is walked (bounded — <see cref="OpcUaBrowseService.TagListMaxNodes"/>), which reports
/// truncation instead of pretending a cut-short list is complete. A driver source has no tag
/// list to read at all, so the import compares against the stored mappings alone and says why.
/// </summary>
static async Task<SourceTagCheck> ReadSourceTagsAsync(
    DaSourceRuntimeSettings source,
    OpcUaBrowseService uaBrowse,
    DaRuntimeSettings settings,
    CancellationToken cancellationToken)
{
    if (string.Equals(source.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
    {
        if (!TryResolveUaConnection(source.SourceId, null, null, null, null, null, settings, out OpcUaSourceClientOptions? options, out string? resolveError))
        {
            return new SourceTagCheck(null, Truncated: false, Error: resolveError);
        }

        UaTagListResult walk = await uaBrowse
            .ListVariableNodeIdsAsync(options!, rootNodeId: null, OpcUaBrowseService.TagListMaxNodes, cancellationToken)
            .ConfigureAwait(false);

        return walk.Error is not null
            ? new SourceTagCheck(null, Truncated: false, Error: walk.Error)
            : new SourceTagCheck(new HashSet<string>(walk.NodeIds, StringComparer.OrdinalIgnoreCase), walk.Truncated, Error: null);
    }

    if (!string.Equals(source.SourceType, SourceTypes.OpcDa, StringComparison.OrdinalIgnoreCase))
    {
        return new SourceTagCheck(
            null,
            Truncated: false,
            Error: "This source type has no tag list to read, so the file is compared with the tags already mapped.");
    }

    if (!OperatingSystem.IsWindows())
    {
        return new SourceTagCheck(null, Truncated: false, Error: "OPC DA browsing requires Windows.");
    }

    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        OpcTagBrowseResult result = await Task.Run(
            () => BrowseDaTags(new DaTagBrowseRequest(
                source.SourceId,
                source.ProgId,
                string.IsNullOrWhiteSpace(source.Host) ? "localhost" : source.Host,
                Path: null,
                Recursive: true,
                source.RemoteUsername,
                source.RemotePassword,
                source.RemoteDomain)),
            cts.Token).ConfigureAwait(false);

        HashSet<string> tags = new(StringComparer.OrdinalIgnoreCase);
        foreach (OpcTagNode tag in result.Tags)
        {
            if (!string.IsNullOrWhiteSpace(tag.ItemId))
            {
                tags.Add(tag.ItemId.Trim());
            }
        }

        return new SourceTagCheck(tags, Truncated: false, Error: null);
    }
    catch (OperationCanceledException)
    {
        return new SourceTagCheck(null, Truncated: false, Error: "Tag browse timed out. Check the server and DCOM settings.");
    }
    catch (Exception exception)
    {
        return new SourceTagCheck(null, Truncated: false, Error: exception.Message);
    }
}

/// <summary>
/// Applies one Windows Firewall rule and turns any failure into a result object — a firewall
/// problem must never fail a port save. The platform guard keeps the Windows-only call honest
/// elsewhere; callers check the platform too, so it is unreachable on other hosts.
/// </summary>
static async Task<object> ApplyFirewallRuleAsync(string ruleName, string description, int port, CancellationToken cancellationToken)
{
    if (!OperatingSystem.IsWindows())
    {
        return new { name = ruleName, port, action = string.Empty, ok = false, error = "Windows Firewall rules apply on Windows hosts only." };
    }

    try
    {
        WindowsFirewall.ApplyResult result = await WindowsFirewall
            .ApplyRuleAsync(ruleName, description, port, cancellationToken)
            .ConfigureAwait(false);
        return new { name = result.Name, port = result.Port, action = result.Action, ok = result.Ok, error = result.Error };
    }
    catch (Exception exception)
    {
        return new { name = ruleName, port, action = string.Empty, ok = false, error = exception.Message };
    }
}


static void TryMigrateLegacyInterlinks(WebApplication app)
{
    string interlinksPath = DataDirectory.Combine("links.json");
    if (File.Exists(interlinksPath))
    {
        return;
    }

    MappingStore mappingStore = app.Services.GetRequiredService<MappingStore>();
    InterlinkStore interlinkStore = app.Services.GetRequiredService<InterlinkStore>();
    (IReadOnlyList<TagMapping> legacyMappings, _) = mappingStore.GetSnapshot();

    DashboardLogStore logStore = app.Services.GetRequiredService<DashboardLogStore>();
    _ = InterlinkApiHelpers.TryMigrateLegacyInterlinks(
        interlinkStore,
        legacyMappings,
        logStore,
        app.Logger,
        out _);
}


static InterlinkDto ToInterlinkDto(InterlinkRule rule)
{
    return new InterlinkDto(
        rule.Id,
        rule.ProviderSourceId,
        rule.ProviderItemId,
        rule.ConsumerSourceId,
        rule.ConsumerItemId,
        rule.Enabled,
        rule.ProviderCanonicalType,
        rule.ConsumerCanonicalType);
}

static bool TryBuildValidatedInterlinkRule(
    InterlinkDto link,
    Guid? routeId,
    MappingStore mappingStore,
    InterlinkStore linkStore,
    IInterlinkMetadataResolver metadataResolver,
    out InterlinkRule rule,
    out string? error)
{
    InterlinkDto normalizedLink = link with
    {
        Id = routeId ?? (link.Id == Guid.Empty ? Guid.NewGuid() : link.Id),
        ProviderSourceId = NormalizeInterlinkSourceId(link.ProviderSourceId),
        ProviderItemId = link.ProviderItemId?.Trim() ?? string.Empty,
        ConsumerSourceId = NormalizeInterlinkSourceId(link.ConsumerSourceId),
        ConsumerItemId = link.ConsumerItemId?.Trim() ?? string.Empty
    };

    // Mapped-tags contract: both endpoints must already exist as enabled tags in
    // Maps, otherwise values could never flow. Checked before live server contact.
    (IReadOnlyList<TagMapping> storedMappings, _) = mappingStore.GetSnapshot();
    if (!InterlinkApiHelpers.TryEnsureSidesAreMapped(
            storedMappings,
            normalizedLink.ProviderSourceId,
            normalizedLink.ProviderItemId,
            normalizedLink.ConsumerSourceId,
            normalizedLink.ConsumerItemId,
            out string? mappedError))
    {
        error = mappedError;
        rule = null!;
        return false;
    }

    (IReadOnlyList<InterlinkRule> rules, _) = linkStore.GetSnapshot();
    bool consumerHasProvider = rules.Any(existing =>
        existing.Id != normalizedLink.Id &&
        string.Equals(existing.ConsumerSourceId, normalizedLink.ConsumerSourceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(existing.ConsumerItemId, normalizedLink.ConsumerItemId, StringComparison.OrdinalIgnoreCase));

    if (!metadataResolver.TryResolve(normalizedLink.ProviderSourceId, normalizedLink.ProviderItemId, out InterlinkTagMetadata providerMetadata))
    {
        error = "Provider tag not found.";
        rule = null!;
        return false;
    }

    if (!metadataResolver.TryResolve(normalizedLink.ConsumerSourceId, normalizedLink.ConsumerItemId, out InterlinkTagMetadata consumerMetadata))
    {
        error = "Consumer tag not found.";
        rule = null!;
        return false;
    }

    InterlinkDto validatedLink = normalizedLink with
    {
        ProviderCanonicalType = providerMetadata.CanonicalType,
        ConsumerCanonicalType = consumerMetadata.CanonicalType,
        ProviderAccessRights = providerMetadata.AccessRights,
        ConsumerAccessRights = consumerMetadata.AccessRights
    };

    error = InterlinkValidators.Validate(validatedLink, consumerHasProvider);
    rule = new InterlinkRule(
        validatedLink.Id,
        validatedLink.ProviderSourceId,
        validatedLink.ProviderItemId,
        validatedLink.ConsumerSourceId,
        validatedLink.ConsumerItemId,
        validatedLink.Enabled,
        validatedLink.ProviderCanonicalType,
        validatedLink.ConsumerCanonicalType);
    return error is null;
}

static string NormalizeInterlinkSourceId(string? sourceId)
{
    string value = sourceId?.Trim() ?? string.Empty;
    return value.Length == 0 ? DaRuntimeSettings.DefaultSourceId : value;
}

static object ToSourceApiDto(DaSourceRuntimeSettings source)
{
    return new
    {
        sourceId = source.SourceId,
        displayName = source.DisplayName,
        sourceType = source.SourceType,
        progId = source.ProgId,
        host = source.Host,
        transport = source.Transport,
        serialPortName = source.SerialPortName,
        baudRate = source.BaudRate,
        dataBits = source.DataBits,
        parity = source.Parity,
        stopBits = source.StopBits,
        stationNo = source.StationNo,
        pcNo = source.PcNo,
        localPpiAddress = source.LocalPpiAddress,
        remotePpiAddress = source.RemotePpiAddress,
        timeoutMs = source.TimeoutMs,
        retryCount = source.RetryCount,
        endpointUrl = source.EndpointUrl,
        securityMode = source.SecurityMode,
        securityPolicy = source.SecurityPolicy,
        updateRateMs = source.UpdateRateMs,
        sessionTimeoutMs = source.SessionTimeoutMs,
        reconnectDelayMs = source.ReconnectDelayMs,
        watchdogTimeoutMs = source.WatchdogTimeoutMs,
        maxMappedTags = source.MaxMappedTags,
        useSubscriptions = source.UseSubscriptions,
        ioMode = source.IoMode,
        remoteUsername = source.RemoteUsername,
        remoteDomain = source.RemoteDomain,
        uaUsername = source.UaUsername
    };
}

static bool TryValidateSourceUpsert(DaServerConfigRequest request, string serverEndpointUrl, DaRuntimeSettings settings, out string? error)
{
    error = null;
    string sourceType = ResolveApiSourceType(request.SourceType, out string? typeError);
    if (typeError is not null)
    {
        error = typeError;
        return false;
    }

    if (string.Equals(sourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
    {
        string endpointUrl = request.EndpointUrl?.Trim() ?? string.Empty;
        if (endpointUrl.Length == 0)
        {
            error = "Endpoint URL is required for OPC UA sources.";
            return false;
        }

        if (!endpointUrl.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase))
        {
            error = "Endpoint URL must start with opc.tcp://.";
            return false;
        }

        if (!TryValidateUaSecurity(request.SecurityMode, request.SecurityPolicy, out string? securityError))
        {
            error = securityError;
            return false;
        }

        if (UaEndpointGuard.TargetsSelf(endpointUrl, serverEndpointUrl))
        {
            error = "Source endpoint cannot target this bridge's own OPC UA server.";
            return false;
        }

        return true;
    }

    if (string.Equals(sourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
    {
        string portError = ValidateMelsecSerialPort(request, settings);
        if (portError.Length > 0)
        {
            error = portError;
            return false;
        }

        return true;
    }

    if (string.Equals(sourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
    {
        string portError = ValidateS7200SerialPort(request, settings);
        if (portError.Length > 0)
        {
            error = portError;
            return false;
        }

        return true;
    }

    if (string.IsNullOrWhiteSpace(request.ProgId))
    {
        error = "ProgId is required for OPC DA sources.";
        return false;
    }

    return true;
}

static string ResolveApiSourceType(string? sourceType, out string? error)
{
    error = null;
    if (string.IsNullOrWhiteSpace(sourceType))
    {
        return SourceTypes.OpcDa;
    }

    string trimmed = sourceType.Trim();
    if (string.Equals(trimmed, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
    {
        return SourceTypes.OpcUa;
    }

    if (string.Equals(trimmed, SourceTypes.OpcDa, StringComparison.OrdinalIgnoreCase))
    {
        return SourceTypes.OpcDa;
    }

    if (string.Equals(trimmed, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
    {
        return SourceTypes.MelsecA3n;
    }

    if (string.Equals(trimmed, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
    {
        return SourceTypes.S7200Ppi;
    }

    error = "Source type must be OpcDa, OpcUa, MelsecA3n, or S7200Ppi.";
    return string.Empty;
}

static bool TryValidateUaSecurity(string? securityMode, string? securityPolicy, out string? error)
{
    error = null;
    string mode = string.IsNullOrWhiteSpace(securityMode) ? "None" : securityMode.Trim();
    string policy = string.IsNullOrWhiteSpace(securityPolicy) ? "None" : securityPolicy.Trim();

    bool modeOk = mode.Equals("None", StringComparison.OrdinalIgnoreCase)
        || mode.Equals("Sign", StringComparison.OrdinalIgnoreCase)
        || mode.Equals("SignAndEncrypt", StringComparison.OrdinalIgnoreCase);
    if (!modeOk)
    {
        error = "Security mode must be None, Sign, or SignAndEncrypt.";
        return false;
    }

    bool policyOk = policy.Equals("None", StringComparison.OrdinalIgnoreCase)
        || policy.Equals("Basic256Sha256", StringComparison.OrdinalIgnoreCase);
    if (!policyOk)
    {
        error = "Security policy must be None or Basic256Sha256.";
        return false;
    }

    bool modeIsNone = mode.Equals("None", StringComparison.OrdinalIgnoreCase);
    bool policyIsNone = policy.Equals("None", StringComparison.OrdinalIgnoreCase);
    if (modeIsNone != policyIsNone)
    {
        error = "Security mode None requires policy None; Sign/SignAndEncrypt require Basic256Sha256.";
        return false;
    }

    if (!modeIsNone && !policy.Equals("Basic256Sha256", StringComparison.OrdinalIgnoreCase))
    {
        error = "Security mode None requires policy None; Sign/SignAndEncrypt require Basic256Sha256.";
        return false;
    }

    return true;
}

static string FirstNonBlank(string? first, string? second)
{
    if (!string.IsNullOrWhiteSpace(first)) return first;
    return second ?? string.Empty;
}

static bool TryResolveUaConnection(
    string? sourceId,
    string? endpointUrl,
    string? securityMode,
    string? securityPolicy,
    string? username,
    string? password,
    DaRuntimeSettings settings,
    out OpcUaSourceClientOptions? options,
    out string? error)
{
    options = null;
    error = null;

    string trimmedSourceId = sourceId?.Trim() ?? string.Empty;
    if (trimmedSourceId.Length > 0)
    {
        DaRuntimeSettingsSnapshot snapshot = settings.GetSnapshot();
        DaSourceRuntimeSettings? source = snapshot.GetSource(trimmedSourceId);
        if (source is null)
        {
            error = $"Source '{trimmedSourceId}' was not found.";
            return false;
        }

        if (!string.Equals(source.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Source '{trimmedSourceId}' is not an OpcUa source.";
            return false;
        }

        // Explicit body fields override stored source values when provided.
        string resolvedEndpoint = !string.IsNullOrWhiteSpace(endpointUrl)
            ? endpointUrl.Trim()
            : source.EndpointUrl;
        string resolvedMode = !string.IsNullOrWhiteSpace(securityMode)
            ? securityMode.Trim()
            : source.SecurityMode;
        string resolvedPolicy = !string.IsNullOrWhiteSpace(securityPolicy)
            ? securityPolicy.Trim()
            : source.SecurityPolicy;
        string? resolvedUser = username ?? source.UaUsername;
        string? resolvedPassword = password ?? source.UaPassword;

        if (!TryValidateUaConnectionFields(resolvedEndpoint, resolvedMode, resolvedPolicy, out error))
        {
            return false;
        }

        options = new OpcUaSourceClientOptions
        {
            SourceId = source.SourceId,
            DisplayName = source.DisplayName,
            EndpointUrl = resolvedEndpoint,
            SecurityMode = string.IsNullOrWhiteSpace(resolvedMode) ? "None" : resolvedMode,
            SecurityPolicy = string.IsNullOrWhiteSpace(resolvedPolicy) ? "None" : resolvedPolicy,
            Username = resolvedUser,
            Password = resolvedPassword,
            SessionTimeoutMs = source.SessionTimeoutMs > 0
                ? source.SessionTimeoutMs
                : OpcUaBrowseService.DefaultTimeoutMs,
            AutoAcceptUntrustedCertificates = true,
            PkiRoot = "pki/ua-client"
        };
        return true;
    }

    string directEndpoint = endpointUrl?.Trim() ?? string.Empty;
    if (!TryValidateUaConnectionFields(directEndpoint, securityMode, securityPolicy, out error))
    {
        return false;
    }

    options = new OpcUaSourceClientOptions
    {
        SourceId = "adhoc",
        DisplayName = "Ad-hoc",
        EndpointUrl = directEndpoint,
        SecurityMode = string.IsNullOrWhiteSpace(securityMode) ? "None" : securityMode.Trim(),
        SecurityPolicy = string.IsNullOrWhiteSpace(securityPolicy) ? "None" : securityPolicy.Trim(),
        Username = username,
        Password = password,
        SessionTimeoutMs = OpcUaBrowseService.DefaultTimeoutMs,
        AutoAcceptUntrustedCertificates = true,
        PkiRoot = "pki/ua-client"
    };
    return true;
}

static bool TryValidateUaConnectionFields(
    string endpointUrl,
    string? securityMode,
    string? securityPolicy,
    out string? error)
{
    error = null;
    if (string.IsNullOrWhiteSpace(endpointUrl))
    {
        error = "Endpoint URL is required (or provide a valid sourceId).";
        return false;
    }

    string trimmed = endpointUrl.Trim();
    if (!trimmed.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase))
    {
        error = "Endpoint URL must start with opc.tcp://.";
        return false;
    }

    if (!TryValidateUaSecurity(securityMode, securityPolicy, out string? securityError))
    {
        error = securityError;
        return false;
    }

    return true;
}

// Insert-only add answers with what it did, so the Maps tab can say "already mapped"
// instead of appearing to ignore the click (issue #7).
static object AddResultPayload(long version, MappingAddResult result) => new
{
    version,
    added = result.Added,
    skippedExisting = result.SkippedExisting,
    existing = ExistingKeysPayload(result)
};

static object[] ExistingKeysPayload(MappingAddResult result) => result.ExistingKeys
    .Select(key => new { sourceId = key.SourceId, itemId = key.ItemId })
    .ToArray<object>();

static string? TryGetMaxMappedTagsError(
    IReadOnlyList<TagMapping> incoming,
    MappingStore store,
    DaRuntimeSettings settings)
{
    if (incoming.Count == 0)
    {
        return null;
    }

    (IReadOnlyList<TagMapping> existing, _) = store.GetSnapshot();
    DaRuntimeSettingsSnapshot snapshot = settings.GetSnapshot();

    Dictionary<string, HashSet<string>> incomingBySource = new(StringComparer.OrdinalIgnoreCase);
    foreach (TagMapping tag in incoming)
    {
        string sourceId = string.IsNullOrWhiteSpace(tag.SourceId)
            ? DaRuntimeSettings.DefaultSourceId
            : tag.SourceId.Trim();
        string itemId = tag.ItemId?.Trim() ?? string.Empty;
        if (itemId.Length == 0)
        {
            continue;
        }

        if (!incomingBySource.TryGetValue(sourceId, out HashSet<string>? items))
        {
            items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            incomingBySource[sourceId] = items;
        }

        items.Add(itemId);
    }

    foreach ((string sourceId, HashSet<string> newItems) in incomingBySource)
    {
        DaSourceRuntimeSettings? source = snapshot.GetSource(sourceId);
        if (source is null
            || !string.Equals(source.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        HashSet<string> existingItems = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < existing.Count; i++)
        {
            TagMapping mapping = existing[i];
            if (string.Equals(mapping.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(mapping.ItemId))
            {
                existingItems.Add(mapping.ItemId);
            }
        }

        int newUnique = 0;
        foreach (string itemId in newItems)
        {
            if (!existingItems.Contains(itemId))
            {
                newUnique++;
            }
        }

        int total = existingItems.Count + newUnique;
        if (total > source.MaxMappedTags)
        {
            return $"Source {source.SourceId} exceeds MaxMappedTags ({source.MaxMappedTags}).";
        }
    }

    return null;
}

static bool TryParseLogLevel(string? value, out LogLevel level)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        level = LogLevel.None;
        return false;
    }

    return Enum.TryParse(value.Trim(), ignoreCase: true, out level);
}

static MqttPayloadField? ParsePayloadFields(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    return Enum.TryParse<MqttPayloadField>(value.Trim(), ignoreCase: true, out MqttPayloadField result)
        ? result
        : null;
}

static string ValidateMelsecSerialPort(DaServerConfigRequest request, DaRuntimeSettings settings)
{
    string port = (request.SerialPortName ?? string.Empty).Trim();
    if (port.Length == 0)
    {
        return "SerialPortName is required for MelsecA3n sources.";
    }

    string transport = (request.Transport ?? string.Empty).Trim();
    if (transport.Length > 0 && !string.Equals(transport, "Serial", StringComparison.OrdinalIgnoreCase))
    {
        return $"MelsecA3n sources only support Transport 'Serial'; '{transport}' is not allowed.";
    }

    // Reject duplicate SerialPortName across other sources (case-sensitive on Linux paths).
    DaRuntimeSettingsSnapshot snapshot = settings.GetSnapshot();
    foreach (DaSourceRuntimeSettings existing in snapshot.Sources)
    {
        if (string.Equals(existing.SourceId, request.SourceId, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        bool isSerialDriver =
            string.Equals(existing.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase)
            || string.Equals(existing.SourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase);
        if (!isSerialDriver)
        {
            continue;
        }

        if (string.Equals(existing.SerialPortName ?? string.Empty, port, StringComparison.Ordinal))
        {
            return $"SerialPortName '{port}' is already used by source '{existing.SourceId}'.";
        }
    }

    return string.Empty;
}

static string ValidateS7200SerialPort(DaServerConfigRequest request, DaRuntimeSettings settings)
{
    string port = (request.SerialPortName ?? string.Empty).Trim();
    if (port.Length == 0)
    {
        return "SerialPortName is required for S7200Ppi sources.";
    }

    string transport = (request.Transport ?? string.Empty).Trim();
    if (transport.Length > 0 && !string.Equals(transport, "Serial", StringComparison.OrdinalIgnoreCase))
    {
        return $"S7200Ppi sources only support Transport 'Serial'; '{transport}' is not allowed.";
    }

    if (request.LocalPpiAddress is < 0 or > 126)
    {
        return "LocalPpiAddress must be between 0 and 126.";
    }

    if (request.RemotePpiAddress is < 0 or > 126)
    {
        return "RemotePpiAddress must be between 0 and 126.";
    }

    DaRuntimeSettingsSnapshot snapshot = settings.GetSnapshot();
    foreach (DaSourceRuntimeSettings existing in snapshot.Sources)
    {
        if (string.Equals(existing.SourceId, request.SourceId, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        bool isSerialDriver =
            string.Equals(existing.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase)
            || string.Equals(existing.SourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase);
        if (!isSerialDriver)
        {
            continue;
        }

        if (string.Equals(existing.SerialPortName ?? string.Empty, port, StringComparison.Ordinal))
        {
            return $"SerialPortName '{port}' is already used by source '{existing.SourceId}'.";
        }
    }

    return string.Empty;
}

static MelsecA3nClientOptions? ResolveMelsecTestOptions(MelsecTestConnectionRequest request, DaRuntimeSettings settings)
{
    // Prefer explicit body fields (Drivers form always sends them) so unsaved edits are tested.
    string port = (request.SerialPortName ?? string.Empty).Trim();
    if (port.Length > 0)
    {
        return new MelsecA3nClientOptions
        {
            SourceId = string.IsNullOrWhiteSpace(request.SourceId) ? "test-connection" : request.SourceId.Trim(),
            SerialPortName = port,
            BaudRate = request.BaudRate is > 0 ? request.BaudRate.Value : 9600,
            DataBits = request.DataBits is 7 or 8 ? request.DataBits.Value : 8,
            Parity = string.IsNullOrWhiteSpace(request.Parity) ? "Odd" : request.Parity!,
            StopBits = string.IsNullOrWhiteSpace(request.StopBits) ? "One" : request.StopBits!,
            StationNo = string.IsNullOrWhiteSpace(request.StationNo) ? "00" : request.StationNo!,
            PcNo = string.IsNullOrWhiteSpace(request.PcNo) ? "FF" : request.PcNo!,
            TimeoutMs = request.TimeoutMs is > 0 ? request.TimeoutMs.Value : 3000,
            RetryCount = request.RetryCount is >= 0 ? request.RetryCount.Value : 0
        };
    }

    if (!string.IsNullOrWhiteSpace(request.SourceId))
    {
        DaSourceRuntimeSettings? source = settings.GetSnapshot().GetSource(request.SourceId);
        if (source is null || !string.Equals(source.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new MelsecA3nClientOptions
        {
            SourceId = source.SourceId,
            SerialPortName = source.SerialPortName,
            BaudRate = source.BaudRate,
            DataBits = source.DataBits,
            Parity = source.Parity,
            StopBits = source.StopBits,
            StationNo = source.StationNo,
            PcNo = source.PcNo,
            TimeoutMs = source.TimeoutMs,
            RetryCount = source.RetryCount
        };
    }

    return null;
}

static TagMapping ToTagMapping(MappingTagDto tag) => new()
{
    SourceId = tag.SourceId,
    ItemId = tag.ItemId,
    DisplayName = tag.DisplayName ?? string.Empty,
    Description = tag.Description,
    DataType = tag.DataType ?? "Auto",
    UaNodeId = tag.UaNodeId ?? string.Empty,
    Enabled = tag.Enabled ?? true,
    Mode = string.IsNullOrWhiteSpace(tag.Mode) ? TagMode.Source : tag.Mode,
    ManualValue = string.IsNullOrWhiteSpace(tag.ManualValue) ? null : tag.ManualValue,
    PollRateMs = tag.PollRateMs ?? 0,
    Decimals = tag.Decimals,
    DeadbandPct = tag.DeadbandPct ?? 0f,
    Writeable = tag.Writeable ?? false,
    AccessRights = tag.AccessRights ?? string.Empty,
    MqttEnabled = tag.MqttEnabled ?? false,
    MqttTopic = string.IsNullOrWhiteSpace(tag.MqttTopic) ? null : tag.MqttTopic,
    InfluxEnabled = tag.InfluxEnabled ?? false,
    Unit = string.IsNullOrWhiteSpace(tag.Unit) ? null : tag.Unit.Trim(),
    RangeMin = tag.RangeMin,
    RangeMax = tag.RangeMax,
    Digital = tag.Digital,
    OnText = string.IsNullOrWhiteSpace(tag.OnText) ? null : tag.OnText.Trim(),
    OffText = string.IsNullOrWhiteSpace(tag.OffText) ? null : tag.OffText.Trim(),
    Subscription = tag.Subscription ?? string.Empty,
    TrendStyle = TrendStyleTypes.Normalize(tag.TrendStyle),
    AddedUtc = tag.AddedUtc
};

static bool ValidateMelsecMappings(List<TagMapping> tags, DaRuntimeSettings daSettings, MappingStore store, out string error)
{
    error = string.Empty;
    DaRuntimeSettingsSnapshot snapshot = daSettings.GetSnapshot();

    // Validate + canonicalize ItemId for every MelsecA3n-bound tag.
    for (int i = 0; i < tags.Count; i++)
    {
        TagMapping tag = tags[i];
        DaSourceRuntimeSettings? source = snapshot.GetSource(tag.SourceId);
        if (source is null || !string.Equals(source.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!MelsecAddressParser.TryParse(tag.ItemId, out MelsecAddress address, out string addrError))
        {
            error = $"Invalid Melsec address '{tag.ItemId}': {addrError}";
            return true;
        }

        tag.ItemId = address.Canonical;
        tags[i] = tag;
    }

    // Enforce MaxMappedTags per MelsecA3n source (existing + new, de-duplicated by key).
    Dictionary<string, int> newPerSource = new(StringComparer.OrdinalIgnoreCase);
    HashSet<(string SourceId, string ItemId)> newKeys = new(StringTupleComparerIgnoreCase.Instance);
    foreach (TagMapping tag in tags)
    {
        DaSourceRuntimeSettings? source = snapshot.GetSource(tag.SourceId);
        if (source is null || !string.Equals(source.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!newKeys.Add((tag.SourceId, tag.ItemId)))
        {
            continue;
        }

        newPerSource[tag.SourceId] = newPerSource.TryGetValue(tag.SourceId, out int c) ? c + 1 : 1;
    }

    foreach (KeyValuePair<string, int> entry in newPerSource)
    {
        DaSourceRuntimeSettings? source = snapshot.GetSource(entry.Key);
        if (source is null)
        {
            continue;
        }

        int existing = store.GetBySource(entry.Key).Count;
        int limit = source.MaxMappedTags > 0 ? source.MaxMappedTags : 2000;
        if (existing + entry.Value > limit)
        {
            error = $"Mapping add would exceed max mapped tags ({limit}) for source '{entry.Key}'.";
            return true;
        }
    }

    return false;
}

static S7200ClientOptions? ResolveS7200TestOptions(S7200TestConnectionRequest request, DaRuntimeSettings settings)
{
    if (!string.IsNullOrWhiteSpace(request.SourceId))
    {
        DaSourceRuntimeSettings? source = settings.GetSnapshot().GetSource(request.SourceId);
        if (source is null || !string.Equals(source.SourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new S7200ClientOptions
        {
            SourceId = source.SourceId,
            SerialPortName = source.SerialPortName,
            BaudRate = source.BaudRate,
            DataBits = source.DataBits,
            Parity = source.Parity,
            StopBits = source.StopBits,
            LocalPpiAddress = source.LocalPpiAddress,
            RemotePpiAddress = source.RemotePpiAddress,
            TimeoutMs = source.TimeoutMs,
            RetryCount = source.RetryCount
        };
    }

    if (string.IsNullOrWhiteSpace(request.SerialPortName))
    {
        return null;
    }

    return new S7200ClientOptions
    {
        SourceId = "test-connection",
        SerialPortName = request.SerialPortName.Trim(),
        BaudRate = request.BaudRate is > 0 ? request.BaudRate.Value : 9600,
        DataBits = request.DataBits is 7 or 8 ? request.DataBits.Value : 8,
        Parity = string.IsNullOrWhiteSpace(request.Parity) ? "Even" : request.Parity!,
        StopBits = string.IsNullOrWhiteSpace(request.StopBits) ? "One" : request.StopBits!,
        LocalPpiAddress = request.LocalPpiAddress ?? 0,
        RemotePpiAddress = request.RemotePpiAddress ?? 2,
        TimeoutMs = request.TimeoutMs is > 0 ? request.TimeoutMs.Value : 3000,
        RetryCount = request.RetryCount is >= 0 ? request.RetryCount.Value : 2
    };
}

static bool ValidateS7Mappings(List<TagMapping> tags, DaRuntimeSettings daSettings, MappingStore store, out string error)
{
    error = string.Empty;
    DaRuntimeSettingsSnapshot snapshot = daSettings.GetSnapshot();

    for (int i = 0; i < tags.Count; i++)
    {
        TagMapping tag = tags[i];
        DaSourceRuntimeSettings? source = snapshot.GetSource(tag.SourceId);
        if (source is null || !string.Equals(source.SourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!S7AddressParser.TryParse(tag.ItemId, out S7Address address, out string addrError))
        {
            error = $"Invalid S7 address '{tag.ItemId}': {addrError}";
            return true;
        }

        tag.ItemId = address.Canonical;
        tags[i] = tag;
    }

    Dictionary<string, int> newPerSource = new(StringComparer.OrdinalIgnoreCase);
    HashSet<(string SourceId, string ItemId)> newKeys = new(StringTupleComparerIgnoreCase.Instance);
    foreach (TagMapping tag in tags)
    {
        DaSourceRuntimeSettings? source = snapshot.GetSource(tag.SourceId);
        if (source is null || !string.Equals(source.SourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!newKeys.Add((tag.SourceId, tag.ItemId)))
        {
            continue;
        }

        newPerSource[tag.SourceId] = newPerSource.TryGetValue(tag.SourceId, out int c) ? c + 1 : 1;
    }

    foreach (KeyValuePair<string, int> entry in newPerSource)
    {
        DaSourceRuntimeSettings? source = snapshot.GetSource(entry.Key);
        if (source is null)
        {
            continue;
        }

        int existing = store.GetBySource(entry.Key).Count;
        int limit = source.MaxMappedTags > 0 ? source.MaxMappedTags : 2000;
        if (existing + entry.Value > limit)
        {
            error = $"Mapping add would exceed max mapped tags ({limit}) for S7200Ppi source '{entry.Key}'.";
            return true;
        }
    }

    return false;
}

internal sealed class StringTupleComparerIgnoreCase : IEqualityComparer<(string SourceId, string ItemId)>
{
    public static StringTupleComparerIgnoreCase Instance { get; } = new();
    public bool Equals((string SourceId, string ItemId) x, (string SourceId, string ItemId) y) =>
        string.Equals(x.SourceId, y.SourceId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(x.ItemId, y.ItemId, StringComparison.OrdinalIgnoreCase);
    public int GetHashCode((string SourceId, string ItemId) value) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.SourceId),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.ItemId));
}

record PortProbeRequest(int Port, string? Kind);

record PortConfigRequest(int HttpPort, int UaPort);

record FirewallApplyRequest(int? HttpPort, int? UaPort);

record MqttConfigRequest(
    bool Enabled,
    string? BrokerUrl,
    string? ClientId,
    string? UserName,
    string? Password,
    bool Tls,
    bool IgnoreCertErrors,
    string? TopicPrefix,
    string? PayloadFields);

record InfluxConfigRequest(
    bool Enabled,
    string? Url,
    string? Org,
    string? Bucket,
    string? Token,
    string? Measurement,
    int? TimeoutMs,
    bool VerifySsl);
