using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

builder.Services.AddSingleton<DaRuntimeSettings>();
builder.Services.AddSingleton<DaClientFactory>();
builder.Services.AddSingleton<BridgeState>();
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
app.MapPost("/api/da/mode", (ModeChangeRequest request, DaRuntimeSettings settings) =>
{
    DaRuntimeSettingsSnapshot snapshot = settings.SetMode(request.Mode);
    return Results.Json(new { mode = snapshot.Mode, version = snapshot.Version });
});
app.MapGet("/api/da/config", (DaRuntimeSettings settings) =>
{
    DaRuntimeSettingsSnapshot snapshot = settings.GetSnapshot();
    return Results.Json(new { progId = snapshot.ProgId, host = snapshot.Host });
});
app.MapPost("/api/da/config", (DaServerConfigRequest request, DaRuntimeSettings settings) =>
{
    DaRuntimeSettingsSnapshot snapshot = settings.SetServerConfig(request.ProgId, request.Host);
    return Results.Json(new { progId = snapshot.ProgId, host = snapshot.Host, version = snapshot.Version });
});
app.MapGet("/api/da/servers", async (string? host) =>
{
    if (!OperatingSystem.IsWindows())
        return Results.Json(new { error = "OPC DA enumeration requires Windows.", servers = Array.Empty<object>() });
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var servers = await Task.Run(() => OpcBridge.Da.OpcServerEnumerator.Enumerate(host), cts.Token);
        return Results.Json(new { servers });
    }
    catch (OperationCanceledException)
    {
        return Results.Json(new { error = "Enumeration timed out. Check OpcEnum service and DCOM settings.", servers = Array.Empty<object>() });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message, servers = Array.Empty<object>() });
    }
});
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync().ConfigureAwait(false);
