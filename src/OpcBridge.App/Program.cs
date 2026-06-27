using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Ua;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:8080");
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection("Bridge"));
builder.Services.Configure<DaClientOptions>(builder.Configuration.GetSection("Da"));
builder.Services.Configure<UaServerOptions>(builder.Configuration.GetSection("Ua"));
builder.Services.AddSingleton<DashboardLogStore>();
builder.Logging.Services.AddSingleton<ILoggerProvider, DashboardLogProvider>();


builder.Services.AddSingleton<DaRuntimeSettings>();
builder.Services.AddSingleton<DaClientFactory>();
builder.Services.AddSingleton<BridgeState>();
builder.Services.AddSingleton<MappingStore>();
builder.Services.AddSingleton<UaServerHost>();
builder.Services.AddHostedService<BridgeWorker>();

WebApplication app = builder.Build();

app.MapGet("/", () => Results.Content(DashboardPage.FullHtml, "text/html"));
app.MapGet("/api/values", (BridgeState state) => Results.Json(new { values = state.GetValues() }));
app.MapGet("/api/status", (BridgeState state, UaServerHost uaServer) => Results.Json(new
{
    bridge = state.GetStatus(),
    ua = uaServer.GetStatus()
}));
app.MapGet("/api/dashboard", (BridgeState state, UaServerHost uaServer) => Results.Json(new
{
    bridge = state.GetStatus(),
    ua = uaServer.GetStatus(),
    values = state.GetValues()
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
    Assembly assembly = typeof(Program).Assembly;
    AssemblyName assemblyName = assembly.GetName();
    return Results.Json(new
    {
        name = assemblyName.Name ?? "OpcBridge.App",
        version = assemblyName.Version?.ToString() ?? "0.0.0.0",
        informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty,
        framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        machineName = Environment.MachineName,
        creator = "xlibraboy"
    });
});
app.MapGet("/api/help", () => Results.Json(new { markdown = HelpContent.Markdown }));
app.MapGet("/api/da/sources", (DaRuntimeSettings settings) =>
{
    DaRuntimeSettingsSnapshot snapshot = settings.GetSnapshot();
    return Results.Json(new
    {
        updateRateMs = snapshot.UpdateRateMs,
        sources = snapshot.Sources.Select(source => new
        {
            sourceId = source.SourceId,
            displayName = source.DisplayName,
            progId = source.ProgId,
            host = source.Host,
            updateRateMs = source.UpdateRateMs,
            remoteUsername = source.RemoteUsername,
            remoteDomain = source.RemoteDomain
        })
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
app.MapPost("/api/da/sources", (DaServerConfigRequest request, DaRuntimeSettings settings) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceId))
    {
        return Results.BadRequest(new { error = "Source ID is required." });
    }
    DaRuntimeSettingsSnapshot snapshot = settings.UpsertSource(new DaSourceRuntimeSettings(
        request.SourceId,
        request.DisplayName ?? string.Empty,
        request.ProgId,
        request.Host,
        request.RemoteUsername,
        request.RemotePassword,
        request.RemoteDomain,
        request.UpdateRateMs));

    DaSourceRuntimeSettings source = snapshot.GetSource(request.SourceId)!;
    return Results.Json(new
    {
        version = snapshot.Version,
        source = new
        {
            sourceId = source.SourceId,
            displayName = source.DisplayName,
            progId = source.ProgId,
            host = source.Host,
            updateRateMs = source.UpdateRateMs,
            remoteUsername = source.RemoteUsername,
            remoteDomain = source.RemoteDomain
        }
    });
});
app.MapPost("/api/da/sources/remove", (DaSourceRemoveRequest request, DaRuntimeSettings settings, MappingStore store) =>
{
    if (!settings.TryRemoveSource(request.SourceId, out DaRuntimeSettingsSnapshot snapshot))
    {
        return Results.BadRequest(new { error = "Cannot remove the last source or source was not found." });
    }

    long mappingVersion = store.RemoveSource(request.SourceId);
    return Results.Json(new { version = snapshot.Version, mappingVersion });
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
        return Results.Json(new { branches = result.Branches, tags = result.Tags });
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
app.MapGet("/api/mappings", (MappingStore store) =>
{
    (IReadOnlyList<TagMapping> mappings, long version) = store.GetSnapshot();
    return Results.Json(new { mappings, version });
});
app.MapPost("/api/mappings/add", (MappingAddRequest request, MappingStore store) =>
{
    if (request.Tags is null || request.Tags.Count == 0)
    {
        return Results.BadRequest(new { error = "At least one mapping is required." });
    }

    if (request.Tags.Any(tag => string.IsNullOrWhiteSpace(tag.SourceId) || string.IsNullOrWhiteSpace(tag.DaItemId)))
    {
        return Results.BadRequest(new { error = "Source ID and DA Item ID are required for every mapping." });
    }

    IEnumerable<TagMapping> tags = request.Tags
        .Select(tag => new TagMapping
        {
            SourceId = tag.SourceId,
            DaItemId = tag.DaItemId,
            DisplayName = tag.DisplayName ?? string.Empty,
            DataType = tag.DataType ?? "Auto",
            UaNodeId = tag.UaNodeId ?? string.Empty,
            Enabled = tag.Enabled ?? true,
            Mode = string.IsNullOrWhiteSpace(tag.Mode) ? TagMode.Source : tag.Mode,
            ManualValue = string.IsNullOrWhiteSpace(tag.ManualValue) ? null : tag.ManualValue,
            PollRateMs = tag.PollRateMs ?? 0
        });

    long version = store.Add(tags);
    return Results.Json(new { version });
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
        DataType = request.Tag.DataType ?? "Auto",
        UaNodeId = request.Tag.UaNodeId ?? string.Empty,
        Enabled = request.Tag.Enabled ?? true,
        Mode = string.IsNullOrWhiteSpace(request.Tag.Mode) ? TagMode.Source : request.Tag.Mode,
        ManualValue = string.IsNullOrWhiteSpace(request.Tag.ManualValue) ? null : request.Tag.ManualValue,
        PollRateMs = request.Tag.PollRateMs ?? 0
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
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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


static bool TryParseLogLevel(string? value, out LogLevel level)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        level = LogLevel.None;
        return false;
    }

    return Enum.TryParse(value.Trim(), ignoreCase: true, out level);
}
