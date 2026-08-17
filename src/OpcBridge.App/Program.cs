using System.Reflection;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using System.IO.Ports;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpcBridge.App;
using OpcBridge.App.Hmi;
using OpcBridge.Client;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Drivers.Melsec;
using OpcBridge.Drivers.Melsec.Addressing;
using OpcBridge.Drivers.MxComponent;
using OpcBridge.Drivers.S7;
using OpcBridge.Drivers.S7.Addressing;
using OpcBridge.Mqtt;
using OpcBridge.Influx;
using OpcBridge.Ua;

// Dashboard UI feed cap: the live-values payload is re-fetched and re-rendered every
// poll cycle; beyond this many values it freezes browsers. UI shows total separately.
const int DashboardValuesLimit = 2000;

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
string cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
JObject? cfg = null;
try { cfg = JObject.Parse(File.ReadAllText(cfgPath)); } catch { }

int savedHttp = cfg?["Bridge"]?["HttpPort"]?.Value<int>() ?? PortHelper.HttpScanStart;
int savedUa = cfg?["Bridge"]?["OpcUaPort"]?.Value<int>() ?? PortHelper.OpcUaScanStart;
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
    cfg ??= new JObject();
    if (cfg["Bridge"] == null) cfg["Bridge"] = new JObject();
    cfg["Bridge"]!["HttpPort"] = httpPort;
    cfg["Bridge"]!["OpcUaPort"] = uaPort;
    if (cfg["Ua"]?["EndpointUrl"] is not null)
        cfg["Ua"]!["EndpointUrl"] = PatchPortInUrl(cfg["Ua"]!["EndpointUrl"]!.ToString(), uaPort);
    File.WriteAllText(cfgPath, cfg.ToString(Newtonsoft.Json.Formatting.Indented));

    if (httpAuto)
        logger.LogWarning("HTTP port {Default} already in use. Auto-assigned to {Port}. appsettings.json updated.", PortHelper.HttpScanStart, httpPort);
    if (uaAuto)
        logger.LogWarning("OPC UA port {Default} already in use. Auto-assigned to {Port}. appsettings.json updated.", PortHelper.OpcUaScanStart, uaPort);

    // Force PKI cert regen when UA port changed
    string certDer = Path.Combine(AppContext.BaseDirectory, "pki", "own", "cert.der");
    if (uaAuto && File.Exists(certDer))
    {
        File.Delete(certDer);
        logger.LogInformation("Deleted pki/own/cert.der to trigger certificate regeneration with new UA port {Port}.", uaPort);
    }
}

BridgeState.ConfigurePorts(httpPort, uaPort, httpAuto, uaAuto);

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

builder.WebHost.UseUrls($"http://0.0.0.0:{httpPort}");
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection("Bridge"));
builder.Services.Configure<DaClientOptions>(builder.Configuration.GetSection("Da"));
builder.Services.Configure<UaServerOptions>(builder.Configuration.GetSection("Ua"));
builder.Services.Configure<MqttBrokerOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<InfluxOptions>(builder.Configuration.GetSection("Influx"));
builder.Services.AddSingleton<DashboardLogStore>();
builder.Logging.Services.AddSingleton<ILoggerProvider, DashboardLogProvider>();


builder.Services.AddSingleton<DaRuntimeSettings>();
builder.Services.AddSingleton<SourceClientFactory>();
builder.Services.AddSingleton<BridgeState>();
builder.Services.AddSingleton<MappingStore>();
builder.Services.AddSingleton<DaLinkStore>();
builder.Services.AddSingleton<IDaLinkMetadataResolver>(sp => sp.GetRequiredService<BridgeWorker>());
builder.Services.AddSingleton<UaServerHost>();
builder.Services.AddSingleton<OpcUaBrowseService>();
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
builder.Services.AddHostedService<HmiBroadcastService>();
builder.Services.AddSingleton<IInfluxTrendQuery>(sp =>
{
    InfluxRuntimeSettings settings = sp.GetRequiredService<InfluxRuntimeSettings>();
    ILogger<InfluxFluxTrendQuery> logger = sp.GetRequiredService<ILogger<InfluxFluxTrendQuery>>();
    return new InfluxFluxTrendQuery(() => settings.GetOptions(), logger);
});


WebApplication app = builder.Build();
TryMigrateLegacyDaLinks(app);

app.MapGet("/", (HttpContext ctx) => {
    ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    ctx.Response.Headers["Pragma"] = "no-cache";
    ctx.Response.Headers["Expires"] = "0";
    return Results.Bytes(System.Text.Encoding.UTF8.GetBytes(DashboardPage.FullHtml), "text/html; charset=utf-8");
});
app.MapGet("/api/values", (BridgeState state) => Results.Json(new { values = state.GetValues() }));
app.MapGet("/api/hmi/tags", (MappingStore mappingStore, BridgeState state) =>
    Results.Json(HmiTagSnapshot.Build(mappingStore, state)));
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
 app.MapGet("/api/status", (BridgeState state, UaServerHost uaServer, BridgeAppDiscovery discovery) => Results.Json(new
 {
     bridge = state.GetStatus(),
     ua = uaServer.GetStatus(),
     apps = discovery.GetStatus()
 }));
app.MapGet("/api/status/ports", () =>
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
        uaClient));
});
 app.MapGet("/api/dashboard", (BridgeState state, UaServerHost uaServer, BridgeAppDiscovery discovery, MappingStore mappingStore, BridgeWorker worker, int? limit, string? sourceId) =>
 {
     IReadOnlyList<BridgeValueSnapshot> values = state.GetValues(limit ?? DashboardValuesLimit, sourceId);

     // Resolve the displayed data type: the runtime type of the actual source
     // value wins; the mapping's configured type is the fallback (read path
     // only — keeps the per-value update hot path untouched).
     (IReadOnlyList<TagMapping> mappings, _) = mappingStore.GetSnapshot();
     Dictionary<string, string> dataTypeByKey = DashboardValues.BuildDataTypeLookup(mappings);

     // Effective update rate per tag: per-tag PollRateMs wins, else the source default.
     Dictionary<string, int> sourceRates = state.GetStatus().Sources
         .GroupBy(source => source.SourceId, StringComparer.OrdinalIgnoreCase)
         .ToDictionary(group => group.Key, group => group.First().UpdateRateMs, StringComparer.OrdinalIgnoreCase);
     Dictionary<string, int> updateRateByKey = DashboardValues.BuildUpdateRateLookup(mappings, sourceRates);

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
         badQuality = state.GetBadQualityTags().Select(tag => new { sourceId = tag.SourceId, itemId = tag.ItemId })
     });
 });
app.MapGet("/api/diagnostics", (BridgeWorker worker, UaServerHost uaServer) => Results.Json(new
{
    bridge = worker.GetDiagnostics(),
    ua = new
    {
        sessions = uaServer.GetSessionDiagnostics(),
        subscriptions = uaServer.GetSubscriptionDiagnostics()
    }
}));
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
 app.MapGet("/api/app-info", () =>
 {
     var info = AppInfoSnapshot.CreateCurrent();
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
    if (request.UpdateRateMs <= 0)
    {
        return Results.BadRequest(new { error = "Update rate must be greater than 0 ms." });
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

    if (request.UpdateRateMs <= 0)
    {
        return Results.BadRequest(new { error = "Update rate must be greater than 0 ms." });
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
app.MapPost("/api/da/sources", (DaServerConfigRequest request, DaRuntimeSettings settings, UaServerHost uaServer) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceId))
    {
        return Results.BadRequest(new { error = "Source ID is required." });
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
    MxComponentSourceOptions? upsertMx = null;
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
    else if (string.Equals(upsertType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
    {
        upsertMx = new MxComponentSourceOptions(
            request.LogicalStationNumber,
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
            request.RemoteDomain);
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
        upsertMx));

    DaSourceRuntimeSettings source = snapshot.GetSource(request.SourceId)!;
    return Results.Json(new
    {
        version = snapshot.Version,
        source = ToSourceApiDto(source)
    });
});
app.MapPost("/api/da/sources/remove", (DaSourceRemoveRequest request, DaRuntimeSettings settings, MappingStore store, DaLinkStore daLinkStore) =>
{
    if (!settings.TryRemoveSource(request.SourceId, out DaRuntimeSettingsSnapshot snapshot))
    {
        return Results.BadRequest(new { error = "Source was not found." });
    }

    long mappingVersion = store.RemoveSource(request.SourceId);
    long daLinkVersion = daLinkStore.RemoveBySource(request.SourceId);
    return Results.Json(new { version = snapshot.Version, mappingVersion, daLinkVersion });
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

app.MapPost("/api/drivers/mx-component/test-connection", async (MxComponentTestConnectionRequest request, DaRuntimeSettings settings) =>
{
    MxComponentClientOptions? options = ResolveMxComponentTestOptions(request, settings);
    if (options is null)
    {
        return Results.Json(new { ok = false, error = "LogicalStationNumber is required, or an existing MxComponent sourceId must be provided." });
    }

    try
    {
        await using MxComponentClient client = new(options);
        await client.ConnectAsync(CancellationToken.None);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
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
app.MapPost("/api/da/tags", async (DaTagBrowseRequest request) =>
{
    if (!OperatingSystem.IsWindows())
    {
        return Results.Json(new { error = "OPC DA browsing requires Windows.", branches = Array.Empty<object>(), tags = Array.Empty<object>() });
    }

    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        OpcTagBrowseResult result = await Task.Run(() => BrowseDaTags(request), cts.Token);
        return Results.Json(new
        {
            branches = result.Branches,
            tags = result.Tags.Select(tag => new
            {
                name = tag.Name,
                itemId = tag.ItemId,
                canonicalDataType = tag.CanonicalDataType,
                accessRights = tag.AccessRights
            })
        });
    }
    catch (OperationCanceledException)
    {
        return Results.Json(new { error = "Tag browse timed out. Check the server and DCOM settings.", branches = Array.Empty<object>(), tags = Array.Empty<object>() });
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message, branches = Array.Empty<object>(), tags = Array.Empty<object>() });
    }
});
app.MapGet("/api/da-links", (DaLinkStore store) =>
{
    (IReadOnlyList<DaLinkRule> rules, long version) = store.GetSnapshot();
    return Results.Json(new
    {
        links = rules.Select(ToDaLinkDto),
        version
    });
});
app.MapPost("/api/da-links", (CreateDaLinkRequest request, DaLinkStore store, IDaLinkMetadataResolver metadataResolver) =>
{
    if (request.Link is null)
    {
        return Results.BadRequest(new { error = "Link is required." });
    }

    if (!TryBuildValidatedDaLinkRule(request.Link, null, store, metadataResolver, out DaLinkRule rule, out string? error))
    {
        return Results.BadRequest(new { error });
    }

    if (!store.TryAdd(rule, out long version, out string? storeError))
    {
        return string.Equals(storeError, "Rule already exists.", StringComparison.Ordinal)
            ? Results.Conflict(new { error = storeError })
            : Results.BadRequest(new { error = storeError });
    }

    return Results.Json(new { link = ToDaLinkDto(rule), version });
});

app.MapPut("/api/da-links/{id:guid}", (Guid id, UpdateDaLinkRequest request, DaLinkStore store, IDaLinkMetadataResolver metadataResolver) =>
{
    if (request.Link is null)
    {
        return Results.BadRequest(new { error = "Link is required." });
    }
    if (!DaLinkApiHelpers.TryGetStoredDaLinkRule(store, id, out _))
    {
        return Results.NotFound(new { error = "Rule not found." });
    }

    if (!TryBuildValidatedDaLinkRule(request.Link, id, store, metadataResolver, out DaLinkRule rule, out string? error))
    {
        return Results.BadRequest(new { error });
    }

    if (!store.TryUpdate(rule, out long version, out string? storeError))
    {
        return string.Equals(storeError, "Rule not found.", StringComparison.Ordinal)
            ? Results.NotFound(new { error = storeError })
            : Results.BadRequest(new { error = storeError });
    }

    return Results.Json(new { link = ToDaLinkDto(rule), version });
});
app.MapDelete("/api/da-links/{id:guid}", (Guid id, DaLinkStore store) =>
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

    long version = store.Add(tags);
    return Results.Json(new { version });
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

    long version = store.Add(tags);
    return Results.Json(new { version, received = request.Tags.Count });
});
app.MapPost("/api/mappings/update", (MappingUpdateRequest request, MappingStore store, DaRuntimeSettings daSettings) =>
{
    if (string.IsNullOrWhiteSpace(request.Tag.SourceId) || string.IsNullOrWhiteSpace(request.Tag.ItemId))
    {
        return Results.BadRequest(new { error = "Source ID and DA Item ID are required." });
    }

    TagMapping tag = ToTagMapping(request.Tag);

    DaSourceRuntimeSettings? source = daSettings.GetSnapshot().GetSource(tag.SourceId);
    if (source is not null && (string.Equals(source.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source.SourceType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase)))
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

app.MapPost("/api/config/import", async (HttpContext context, DaRuntimeSettings daSettings, MappingStore mappingStore) =>
{
    try
    {
        using JsonDocument doc = await JsonDocument.ParseAsync(context.Request.Body);
        JsonElement root = doc.RootElement;

        // Restore DA sources
        if (root.TryGetProperty("daSources", out JsonElement daSourcesEl))
        {
            int updateRate = daSourcesEl.TryGetProperty("updateRateMs", out JsonElement ur) ? ur.GetInt32() : 1000;
            List<DaSourceRuntimeSettings> sources = new();

            if (daSourcesEl.TryGetProperty("sources", out JsonElement sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement s in sourcesEl.EnumerateArray())
                {
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
                        LogicalStationNumber = s.TryGetProperty("logicalStationNumber", out JsonElement lsn) ? lsn.GetInt32() : 0,
                        EndpointUrl = s.TryGetProperty("endpointUrl", out JsonElement eu) ? eu.GetString() : string.Empty,
                        SecurityMode = s.TryGetProperty("securityMode", out JsonElement sm) ? sm.GetString() : string.Empty,
                        SecurityPolicy = s.TryGetProperty("securityPolicy", out JsonElement sp) ? sp.GetString() : string.Empty,
                        UaUsername = s.TryGetProperty("uaUsername", out JsonElement uu) ? uu.GetString() : null,
                        UaPassword = null, // UA password not exported
                        SessionTimeoutMs = s.TryGetProperty("sessionTimeoutMs", out JsonElement sto) ? sto.GetInt32() : 0,
                        ReconnectDelayMs = s.TryGetProperty("reconnectDelayMs", out JsonElement rcd) ? rcd.GetInt32() : 0,
                        MaxMappedTags = s.TryGetProperty("maxMappedTags", out JsonElement mmt) ? mmt.GetInt32() : 0,
                        UseSubscriptions = s.TryGetProperty("useSubscriptions", out JsonElement usrc) ? usrc.GetBoolean() : true,
                        UpdateRateMs = s.TryGetProperty("updateRateMs", out JsonElement sur) ? sur.GetInt32() : updateRate,
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
                            : null,
                        MxComponent = s.TryGetProperty("mxComponent", out JsonElement mxEl) && mxEl.ValueKind == JsonValueKind.Object
                            ? new MxComponentSourceOptionsDto
                            {
                                LogicalStationNumber = mxEl.TryGetProperty("logicalStationNumber", out JsonElement xlsn) ? xlsn.GetInt32() : 0,
                                TimeoutMs = mxEl.TryGetProperty("timeoutMs", out JsonElement xto) ? xto.GetInt32() : 0,
                                RetryCount = mxEl.TryGetProperty("retryCount", out JsonElement xrc) ? xrc.GetInt32() : -1
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
                    Writeable = m.TryGetProperty("writeable", out JsonElement wr) ? wr.GetBoolean() : false
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
    string pkiRoot = Path.Combine(AppContext.BaseDirectory, "pki");
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

    string rejectedPath = Path.Combine(AppContext.BaseDirectory, "pki", "rejected", fileName);
    string trustedPath = Path.Combine(AppContext.BaseDirectory, "pki", "trusted", fileName);

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

    string trustedPath = Path.Combine(AppContext.BaseDirectory, "pki", "trusted", fileName);
    string rejectedPath = Path.Combine(AppContext.BaseDirectory, "pki", "rejected", fileName);

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

    string path = Path.Combine(AppContext.BaseDirectory, "pki", folder, fileName);
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
        UaServerOptions updated = new()
        {
            ApplicationName = current.ApplicationName,
            EndpointUrl = root.TryGetProperty("endpointUrl", out var ep) ? ep.GetString() ?? current.EndpointUrl : current.EndpointUrl,
            AutoAcceptUntrustedCertificates = root.TryGetProperty("autoAcceptUntrustedCertificates", out var aa) ? aa.GetBoolean() : current.AutoAcceptUntrustedCertificates,
            RequireAuthentication = root.TryGetProperty("requireAuthentication", out var ra) ? ra.GetBoolean() : current.RequireAuthentication,
            Username = root.TryGetProperty("username", out var un) ? un.GetString() : current.Username,
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
app.MapHub<HmiHub>("/hmi");

await app.RunAsync().ConfigureAwait(false);

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


static void TryMigrateLegacyDaLinks(WebApplication app)
{
    string daLinksPath = Path.Combine(AppContext.BaseDirectory, "links.json");
    if (File.Exists(daLinksPath))
    {
        return;
    }

    MappingStore mappingStore = app.Services.GetRequiredService<MappingStore>();
    DaLinkStore daLinkStore = app.Services.GetRequiredService<DaLinkStore>();
    (IReadOnlyList<TagMapping> legacyMappings, _) = mappingStore.GetSnapshot();

    DashboardLogStore logStore = app.Services.GetRequiredService<DashboardLogStore>();
    _ = DaLinkApiHelpers.TryMigrateLegacyDaLinks(
        daLinkStore,
        legacyMappings,
        logStore,
        app.Logger,
        out _);
}


static DaLinkDto ToDaLinkDto(DaLinkRule rule)
{
    return new DaLinkDto(
        rule.Id,
        rule.ProviderSourceId,
        rule.ProviderItemId,
        rule.ConsumerSourceId,
        rule.ConsumerItemId,
        rule.Enabled,
        rule.ProviderCanonicalType,
        rule.ConsumerCanonicalType);
}

static bool TryBuildValidatedDaLinkRule(
    DaLinkDto link,
    Guid? routeId,
    DaLinkStore linkStore,
    IDaLinkMetadataResolver metadataResolver,
    out DaLinkRule rule,
    out string? error)
{
    DaLinkDto normalizedLink = link with
    {
        Id = routeId ?? (link.Id == Guid.Empty ? Guid.NewGuid() : link.Id),
        ProviderSourceId = NormalizeDaLinkSourceId(link.ProviderSourceId),
        ProviderItemId = link.ProviderItemId?.Trim() ?? string.Empty,
        ConsumerSourceId = NormalizeDaLinkSourceId(link.ConsumerSourceId),
        ConsumerItemId = link.ConsumerItemId?.Trim() ?? string.Empty
    };

    (IReadOnlyList<DaLinkRule> rules, _) = linkStore.GetSnapshot();
    bool consumerHasProvider = rules.Any(existing =>
        existing.Id != normalizedLink.Id &&
        string.Equals(existing.ConsumerSourceId, normalizedLink.ConsumerSourceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(existing.ConsumerItemId, normalizedLink.ConsumerItemId, StringComparison.OrdinalIgnoreCase));

    if (!metadataResolver.TryResolve(normalizedLink.ProviderSourceId, normalizedLink.ProviderItemId, out DaTagMetadata providerMetadata))
    {
        error = "Provider tag not found.";
        rule = null!;
        return false;
    }

    if (!metadataResolver.TryResolve(normalizedLink.ConsumerSourceId, normalizedLink.ConsumerItemId, out DaTagMetadata consumerMetadata))
    {
        error = "Consumer tag not found.";
        rule = null!;
        return false;
    }

    DaLinkDto validatedLink = normalizedLink with
    {
        ProviderCanonicalType = providerMetadata.CanonicalType,
        ConsumerCanonicalType = consumerMetadata.CanonicalType,
        ProviderAccessRights = providerMetadata.AccessRights,
        ConsumerAccessRights = consumerMetadata.AccessRights
    };

    error = DaLinkValidators.Validate(validatedLink, consumerHasProvider);
    rule = new DaLinkRule(
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

static string NormalizeDaLinkSourceId(string? sourceId)
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
        maxMappedTags = source.MaxMappedTags,
        useSubscriptions = source.UseSubscriptions,
        remoteUsername = source.RemoteUsername,
        remoteDomain = source.RemoteDomain,
        uaUsername = source.UaUsername,
        logicalStationNumber = source.LogicalStationNumber
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

    if (string.Equals(sourceType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
    {
        if (request.LogicalStationNumber is < 0 or > 1023)
        {
            error = "LogicalStationNumber must be between 0 and 1023 (configure the station in MX Component's Communication Settings Utility).";
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

    if (string.Equals(trimmed, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
    {
        return SourceTypes.MxComponent;
    }

    error = "Source type must be OpcDa, OpcUa, MelsecA3n, S7200Ppi, or MxComponent.";
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

static MxComponentClientOptions? ResolveMxComponentTestOptions(MxComponentTestConnectionRequest request, DaRuntimeSettings settings)
{
    // Prefer explicit body fields (Drivers form always sends them) so unsaved edits are tested.
    if (request.LogicalStationNumber is not null)
    {
        return new MxComponentClientOptions
        {
            SourceId = string.IsNullOrWhiteSpace(request.SourceId) ? "test-connection" : request.SourceId.Trim(),
            LogicalStationNumber = request.LogicalStationNumber.Value,
            TimeoutMs = request.TimeoutMs is > 0 ? request.TimeoutMs.Value : 3000,
            RetryCount = request.RetryCount is >= 0 ? request.RetryCount.Value : 0
        };
    }

    if (!string.IsNullOrWhiteSpace(request.SourceId))
    {
        DaSourceRuntimeSettings? source = settings.GetSnapshot().GetSource(request.SourceId);
        if (source is null || !string.Equals(source.SourceType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new MxComponentClientOptions
        {
            SourceId = source.SourceId,
            LogicalStationNumber = source.LogicalStationNumber,
            TimeoutMs = source.MxComponentTimeoutMs,
            RetryCount = source.MxComponentRetryCount
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
    DeadbandPct = tag.DeadbandPct ?? 0f,
    Writeable = tag.Writeable ?? false,
    AccessRights = tag.AccessRights ?? string.Empty,
    MqttEnabled = tag.MqttEnabled ?? false,
    MqttTopic = string.IsNullOrWhiteSpace(tag.MqttTopic) ? null : tag.MqttTopic,
    InfluxEnabled = tag.InfluxEnabled ?? false
};

static bool ValidateMelsecMappings(List<TagMapping> tags, DaRuntimeSettings daSettings, MappingStore store, out string error)
{
    error = string.Empty;
    DaRuntimeSettingsSnapshot snapshot = daSettings.GetSnapshot();

    // Validate + canonicalize ItemId for every MelsecA3n / MxComponent-bound tag (same A3N address space).
    for (int i = 0; i < tags.Count; i++)
    {
        TagMapping tag = tags[i];
        DaSourceRuntimeSettings? source = snapshot.GetSource(tag.SourceId);
        if (source is null || !IsMelsecAddressSource(source))
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

    // Enforce MaxMappedTags per MelsecA3n / MxComponent source (existing + new, de-duplicated by key).
    Dictionary<string, int> newPerSource = new(StringComparer.OrdinalIgnoreCase);
    HashSet<(string SourceId, string ItemId)> newKeys = new(StringTupleComparerIgnoreCase.Instance);
    foreach (TagMapping tag in tags)
    {
        DaSourceRuntimeSettings? source = snapshot.GetSource(tag.SourceId);
        if (source is null || !IsMelsecAddressSource(source))
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

static bool IsMelsecAddressSource(DaSourceRuntimeSettings source)
{
    return string.Equals(source.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source.SourceType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase);
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

/// <summary>
/// Replaces the port in a URL string (e.g. opc.tcp://0.0.0.0:4840/...).
/// Handles URLs with or without explicit port.
/// </summary>
static string PatchPortInUrl(string url, int port)
{
    if (string.IsNullOrEmpty(url)) return url;
    try
    {
        var uri = new Uri(url);
        var builder = new UriBuilder(uri) { Port = port };
        return builder.Uri.ToString().TrimEnd('/');
    }
    catch
    {
        // Fallback: manual replacement
        int lastColon = url.LastIndexOf(':');
        int lastSlash = url.LastIndexOf('/');
        if (lastColon > lastSlash && int.TryParse(url[(lastColon + 1)..], out _))
            return url[..(lastColon + 1)] + port + url[(url.IndexOf('/', lastColon)..)];
        // No port in URL — append it
        return url.TrimEnd('/') + $":{port}";
    }
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
