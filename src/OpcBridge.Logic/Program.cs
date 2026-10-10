using OpcBridge.Client;
using OpcBridge.Core;
using OpcBridge.Logic;

// The app directory is the content root: the app is started from its own folder (or as a
// service), never from a caller's working directory, so appsettings.json resolves there.
WebApplicationOptions options = new() { ContentRootPath = AppContext.BaseDirectory, Args = args };
WebApplicationBuilder builder = WebApplication.CreateBuilder(options);

int preferredPort = builder.Configuration.GetValue("Logic:HttpPort", 8090);
int httpPort = preferredPort > 0 ? preferredPort : 8090;
bool portRolled = false;
if (!PortHelper.IsPortAvailable(httpPort))
{
    int rolled = PortHelper.FindAvailablePort(httpPort, httpPort + 100);
    if (rolled > 0 && rolled != httpPort)
    {
        httpPort = rolled;
        portRolled = true;
    }
}

builder.WebHost.UseUrls($"http://0.0.0.0:{httpPort}");
builder.Services.AddSingleton<BridgeClient>();

WebApplication app = builder.Build();
if (portRolled)
{
    app.Logger.LogWarning("Port {Preferred} is busy; OpcBridge Logic is serving on {Port} instead.", preferredPort, httpPort);
}

app.Logger.LogInformation("OpcBridge Logic is serving on http://0.0.0.0:{Port}.", httpPort);

app.MapGet("/", (HttpContext context) =>
{
    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    return Results.Bytes(System.Text.Encoding.UTF8.GetBytes(LogicAppPage.FullHtml), "text/html; charset=utf-8");
});

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    app = "OpcBridge.Logic",
    version = ReleaseNotes.InformationalVersion(typeof(LogicAppPage).Assembly)
}));

app.MapGet("/api/version", () => Results.Json(new
{
    version = ReleaseNotes.InformationalVersion(typeof(LogicAppPage).Assembly)
}));

app.MapGet("/api/help", () => Results.Json(new { markdown = HelpContent.Markdown }));

app.MapGet("/api/changelog", () =>
{
    string markdown = ReleaseNotes.Load(typeof(LogicAppPage).Assembly, "OpcBridge.Logic.CHANGELOG.md");
    return Results.Json(new { markdown, version = ReleaseNotes.ParseLatestVersion(markdown) });
});

app.MapGet("/api/bridge/status", async (BridgeClient bridge, CancellationToken ct) =>
{
    (string? baseUrl, bool reachable) = await bridge.StatusAsync(ct);
    return Results.Json(new { baseUrl, reachable });
});

app.MapGet("/api/logic", (BridgeClient bridge, HttpContext context, CancellationToken ct) =>
    ForwardAsync(bridge, HttpMethod.Get, "/api/logic", context, ct));
app.MapPost("/api/logic/blocks", (BridgeClient bridge, HttpContext context, CancellationToken ct) =>
    ForwardAsync(bridge, HttpMethod.Post, "/api/logic/blocks", context, ct));
app.MapDelete("/api/logic/blocks/{id}", (string id, BridgeClient bridge, HttpContext context, CancellationToken ct) =>
    ForwardAsync(bridge, HttpMethod.Delete, "/api/logic/blocks/" + Uri.EscapeDataString(id), context, ct));
app.MapGet("/api/logic/state", (BridgeClient bridge, HttpContext context, CancellationToken ct) =>
    ForwardAsync(bridge, HttpMethod.Get, "/api/logic/state", context, ct));
app.MapGet("/api/hmi/tags", (BridgeClient bridge, HttpContext context, CancellationToken ct) =>
    ForwardAsync(bridge, HttpMethod.Get, "/api/hmi/tags", context, ct));

app.Run();

// The SPA asks this app for the bridge's own paths; each call is forwarded and the bridge's
// status code and body are returned verbatim, so the SPA renders the bridge's errors (409 name
// conflict, 400 rejected save) without knowing this app exists.
static async Task ForwardAsync(BridgeClient bridge, HttpMethod method, string path, HttpContext context, CancellationToken ct)
{
    string? body = null;
    if (method != HttpMethod.Get && method != HttpMethod.Delete)
    {
        using var reader = new StreamReader(context.Request.Body);
        body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    ForwardedResponse response = await bridge.ForwardAsync(method, path, body, context.Request.ContentType, ct).ConfigureAwait(false);
    context.Response.StatusCode = response.StatusCode;
    context.Response.ContentType = response.ContentType;
    await context.Response.Body.WriteAsync(response.Body, ct).ConfigureAwait(false);
}
