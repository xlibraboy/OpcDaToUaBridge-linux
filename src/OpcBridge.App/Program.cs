using System.Reflection;
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
using OpcBridge.Mqtt;
using OpcBridge.Influx;
using OpcBridge.Ua;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:8080");
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection("Bridge"));
builder.Services.Configure<DaClientOptions>(builder.Configuration.GetSection("Da"));
builder.Services.Configure<UaServerOptions>(builder.Configuration.GetSection("Ua"));
builder.Services.Configure<MqttBrokerOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<InfluxOptions>(builder.Configuration.GetSection("Influx"));
builder.Services.AddSingleton<DashboardLogStore>();
builder.Logging.Services.AddSingleton<ILoggerProvider, DashboardLogProvider>();


builder.Services.AddSingleton<DaRuntimeSettings>();
builder.Services.AddSingleton<DaClientFactory>();
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
 builder.Services.AddSingleton<BridgeAppDiscovery>();
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
        request.DaItemId ?? string.Empty,
        request.Value,
        ct).ConfigureAwait(false);
    return Results.Json(new HmiWriteResponse { Ok = ok, Error = error });
});
app.MapGet("/api/hmi/trends", async (
    string? sourceId,
    string? daItemId,
    DateTime? from,
    DateTime? to,
    int? maxPoints,
    IInfluxTrendQuery trends,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(daItemId))
    {
        return Results.Json(
            new { error = "sourceId and daItemId are required" },
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
        daItemId.Trim(),
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
 app.MapGet("/api/dashboard", (BridgeState state, UaServerHost uaServer, BridgeAppDiscovery discovery) => Results.Json(new
 {
     bridge = state.GetStatus(),
     ua = uaServer.GetStatus(),
     apps = discovery.GetStatus(),
     values = state.GetValues()
 }));
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

    if (!TryValidateSourceUpsert(request, uaServer.GetOptions().EndpointUrl, out string? validationError))
    {
        return Results.BadRequest(new { error = validationError });
    }

    DaRuntimeSettingsSnapshot snapshot = settings.UpsertSource(new DaSourceRuntimeSettings(
        request.SourceId,
        request.DisplayName ?? string.Empty,
        request.SourceType ?? string.Empty,
        request.ProgId ?? string.Empty,
        request.Host ?? string.Empty,
        request.RemoteUsername,
        request.RemotePassword,
        request.RemoteDomain,
        request.EndpointUrl ?? string.Empty,
        request.SecurityMode ?? string.Empty,
        request.SecurityPolicy ?? string.Empty,
        request.UaUsername,
        request.UaPassword,
        request.SessionTimeoutMs,
        request.ReconnectDelayMs,
        request.MaxMappedTags,
        request.UseSubscriptions ?? true,
        request.UpdateRateMs));

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
        return Results.BadRequest(new { error = "Cannot remove the last source or source was not found." });
    }

    long mappingVersion = store.RemoveSource(request.SourceId);
    long daLinkVersion = daLinkStore.RemoveBySource(request.SourceId);
    return Results.Json(new { version = snapshot.Version, mappingVersion, daLinkVersion });
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

    if (request.Tags.Any(tag => string.IsNullOrWhiteSpace(tag.SourceId) || string.IsNullOrWhiteSpace(tag.DaItemId)))
    {
        return Results.BadRequest(new { error = "Source ID and DA Item ID are required for every mapping." });
    }

    List<TagMapping> tags = request.Tags
        .Select(tag => new TagMapping
        {
            SourceId = tag.SourceId,
            DaItemId = tag.DaItemId,
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
            AccessRights = string.IsNullOrWhiteSpace(tag.AccessRights) ? TagAccessRights.Read : tag.AccessRights,
            MqttEnabled = tag.MqttEnabled ?? false,
            MqttTopic = string.IsNullOrWhiteSpace(tag.MqttTopic) ? null : tag.MqttTopic,
            InfluxEnabled = tag.InfluxEnabled ?? false
        })
        .ToList();

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
        .Select(tag => new TagMapping
        {
            SourceId = string.IsNullOrWhiteSpace(tag.SourceId) ? "default" : tag.SourceId,
            DaItemId = tag.DaItemId ?? string.Empty,
            Description = tag.Description,
            DisplayName = tag.DisplayName ?? string.Empty,
            DataType = tag.DataType ?? "Auto",
            UaNodeId = tag.UaNodeId ?? string.Empty,
            Enabled = tag.Enabled ?? true,
            Mode = string.IsNullOrWhiteSpace(tag.Mode) ? TagMode.Source : tag.Mode,
            ManualValue = string.IsNullOrWhiteSpace(tag.ManualValue) ? null : tag.ManualValue,
            PollRateMs = tag.PollRateMs ?? 0,
            Writeable = tag.Writeable ?? false,
            DeadbandPct = tag.DeadbandPct ?? 0f,
            AccessRights = string.IsNullOrWhiteSpace(tag.AccessRights) ? TagAccessRights.Read : tag.AccessRights,
            MqttEnabled = tag.MqttEnabled ?? false,
            MqttTopic = string.IsNullOrWhiteSpace(tag.MqttTopic) ? null : tag.MqttTopic,
            InfluxEnabled = tag.InfluxEnabled ?? false
        })
        .Where(tag => !string.IsNullOrWhiteSpace(tag.DaItemId))
        .ToList();

    if (TryGetMaxMappedTagsError(tags, store, settings) is { } maxError)
    {
        return Results.BadRequest(new { error = maxError });
    }

    long version = store.Add(tags);
    return Results.Json(new { version, received = request.Tags.Count });
});
app.MapPost("/api/mappings/update", (MappingUpdateRequest request, MappingStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Tag.SourceId) || string.IsNullOrWhiteSpace(request.Tag.DaItemId))
    {
        return Results.BadRequest(new { error = "Source ID and DA Item ID are required." });
    }

    TagMapping tag = new()
    {
        SourceId = request.Tag.SourceId,
        DaItemId = request.Tag.DaItemId,
        DisplayName = request.Tag.DisplayName ?? string.Empty,
        Description = request.Tag.Description,
        DataType = request.Tag.DataType ?? "Auto",
        UaNodeId = request.Tag.UaNodeId ?? string.Empty,
        Enabled = request.Tag.Enabled ?? true,
        Mode = string.IsNullOrWhiteSpace(request.Tag.Mode) ? TagMode.Source : request.Tag.Mode,
        ManualValue = string.IsNullOrWhiteSpace(request.Tag.ManualValue) ? null : request.Tag.ManualValue,
        PollRateMs = request.Tag.PollRateMs ?? 0,
        DeadbandPct = request.Tag.DeadbandPct ?? 0f,
        Writeable = request.Tag.Writeable ?? false,
        AccessRights = string.IsNullOrWhiteSpace(request.Tag.AccessRights) ? TagAccessRights.Read : request.Tag.AccessRights,
        MqttEnabled = request.Tag.MqttEnabled ?? false,
        MqttTopic = string.IsNullOrWhiteSpace(request.Tag.MqttTopic) ? null : request.Tag.MqttTopic,
        InfluxEnabled = request.Tag.InfluxEnabled ?? false
    };

    if (!store.TryUpdate(tag, out long version))
    {
        return Results.NotFound(new { error = "Mapping not found." });
    }

    return Results.Json(new { version });
});
app.MapPost("/api/mappings/remove", (MappingRemoveRequest request, MappingStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceId) || string.IsNullOrWhiteSpace(request.DaItemId))
    {
        return Results.BadRequest(new { error = "Source ID and DA Item ID are required." });
    }

    long version = store.Remove(request.SourceId, request.DaItemId);
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
            sources = daSnapshot.Sources.Select(s => new
            {
                sourceId = s.SourceId,
                displayName = s.DisplayName,
                sourceType = s.SourceType,
                progId = s.ProgId,
                host = s.Host,
                endpointUrl = s.EndpointUrl,
                securityMode = s.SecurityMode,
                securityPolicy = s.SecurityPolicy,
                updateRateMs = s.UpdateRateMs,
                sessionTimeoutMs = s.SessionTimeoutMs,
                reconnectDelayMs = s.ReconnectDelayMs,
                maxMappedTags = s.MaxMappedTags,
                useSubscriptions = s.UseSubscriptions,
                remoteUsername = s.RemoteUsername,
                remoteDomain = s.RemoteDomain,
                uaUsername = s.UaUsername
            })
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
                    sources.Add(SourceConfigMigration.Normalize(new DaSourceRuntimeSettings(
                        s.TryGetProperty("sourceId", out JsonElement sid) ? sid.GetString() ?? "default" : "default",
                        s.TryGetProperty("displayName", out JsonElement dn) ? dn.GetString() ?? string.Empty : string.Empty,
                        s.TryGetProperty("sourceType", out JsonElement st) ? st.GetString() ?? string.Empty : string.Empty,
                        s.TryGetProperty("progId", out JsonElement pid) ? pid.GetString() ?? string.Empty : string.Empty,
                        s.TryGetProperty("host", out JsonElement h) ? h.GetString() ?? "localhost" : "localhost",
                        s.TryGetProperty("remoteUsername", out JsonElement ru) ? ru.GetString() : null,
                        null, // password not exported — must be re-entered on import
                        s.TryGetProperty("remoteDomain", out JsonElement rd) ? rd.GetString() : null,
                        s.TryGetProperty("endpointUrl", out JsonElement eu) ? eu.GetString() ?? string.Empty : string.Empty,
                        s.TryGetProperty("securityMode", out JsonElement sm) ? sm.GetString() ?? string.Empty : string.Empty,
                        s.TryGetProperty("securityPolicy", out JsonElement sp) ? sp.GetString() ?? string.Empty : string.Empty,
                        s.TryGetProperty("uaUsername", out JsonElement uu) ? uu.GetString() : null,
                        null, // UA password not exported
                        s.TryGetProperty("sessionTimeoutMs", out JsonElement sto) ? sto.GetInt32() : 0,
                        s.TryGetProperty("reconnectDelayMs", out JsonElement rcd) ? rcd.GetInt32() : 0,
                        s.TryGetProperty("maxMappedTags", out JsonElement mmt) ? mmt.GetInt32() : 0,
                        s.TryGetProperty("useSubscriptions", out JsonElement usrc) ? usrc.GetBoolean() : true,
                        s.TryGetProperty("updateRateMs", out JsonElement sur) ? sur.GetInt32() : updateRate), updateRate));
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
                    DaItemId = m.TryGetProperty("daItemId", out JsonElement di) ? di.GetString() ?? string.Empty : string.Empty,
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
        uaUsername = source.UaUsername
    };
}

static bool TryValidateSourceUpsert(DaServerConfigRequest request, string serverEndpointUrl, out string? error)
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

    error = "Source type must be OpcDa or OpcUa.";
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
        string itemId = tag.DaItemId?.Trim() ?? string.Empty;
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
                && !string.IsNullOrWhiteSpace(mapping.DaItemId))
            {
                existingItems.Add(mapping.DaItemId);
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
