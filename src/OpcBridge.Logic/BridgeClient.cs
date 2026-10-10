using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpcBridge.Logic;

/// <summary>A bridge response passed to the browser verbatim.</summary>
internal sealed record ForwardedResponse(int StatusCode, string ContentType, byte[] Body)
{
    public static ForwardedResponse Json(int statusCode, string json) =>
        new(statusCode, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
}

/// <summary>
/// The server-side bridge client. The browser only ever talks to this app (same-origin), so
/// this client owns the bridge address — the configured one when set, otherwise the one
/// discovery finds — and forwards calls and responses verbatim, which keeps the SPA's error
/// handling the bridge's error handling (the 409 on a duplicate name, the 400 on a rejected
/// save). A bridge restarted on another port is rediscovered on the next failed call.
/// </summary>
public sealed class BridgeClient : IDisposable
{
    private static readonly TimeSpan ResolveRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<BridgeClient> logger_;
    private readonly string configuredUrl_;
    private readonly HttpClient probe_;
    private readonly SemaphoreSlim gate_ = new(1, 1);
    private HttpClient? client_;
    private string? baseUrl_;
    private DateTime lastResolveUtc_ = DateTime.MinValue;

    public BridgeClient(IConfiguration configuration, ILogger<BridgeClient> logger)
    {
        logger_ = logger;
        configuredUrl_ = (configuration["Bridge:BaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
        probe_ = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The bridge address in use; null until a bridge has been found.</summary>
    public string? BaseUrl => Volatile.Read(ref baseUrl_);

    /// <summary>Whether the last forwarded call reached the bridge.</summary>
    public bool Reachable { get; private set; }

    internal async Task<ForwardedResponse> ForwardAsync(HttpMethod method, string path, string? body, string? contentType, CancellationToken ct)
    {
        await EnsureResolvedAsync(ct).ConfigureAwait(false);
        ForwardedResponse? response = await TryForwardAsync(method, path, body, contentType, ct).ConfigureAwait(false);
        if (response is not null)
        {
            return response;
        }

        // The bridge may have moved (a restart picked another port). Rediscover and retry once,
        // but no more often than ResolveRetry, so a down bridge is not swept on every poll.
        if (DateTime.UtcNow - lastResolveUtc_ >= ResolveRetry)
        {
            lastResolveUtc_ = DateTime.UtcNow;
            await ResetAddressAsync(ct).ConfigureAwait(false);
            await EnsureResolvedAsync(ct).ConfigureAwait(false);
            response = await TryForwardAsync(method, path, body, contentType, ct).ConfigureAwait(false);
            if (response is not null)
            {
                return response;
            }
        }

        string at = BaseUrl is { Length: > 0 } url ? url : (configuredUrl_.Length > 0 ? configuredUrl_ : "the bridge");
        return ForwardedResponse.Json(503, JsonSerializer.Serialize(new { error = $"Bridge unreachable at {at}." }));
    }

    /// <summary>The current address plus a live probe, for the header's status readout.</summary>
    public async Task<(string? BaseUrl, bool Reachable)> StatusAsync(CancellationToken ct)
    {
        ForwardedResponse response = await ForwardAsync(HttpMethod.Get, "/api/auth/me", null, null, ct).ConfigureAwait(false);
        return (BaseUrl, response.StatusCode is >= 200 and < 500);
    }

    public void Dispose()
    {
        client_?.Dispose();
        probe_.Dispose();
        gate_.Dispose();
    }

    private async Task EnsureResolvedAsync(CancellationToken ct)
    {
        if (BaseUrl is not null || DateTime.UtcNow - lastResolveUtc_ < ResolveRetry)
        {
            return;
        }

        await gate_.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (BaseUrl is not null)
            {
                return;
            }

            string? url = configuredUrl_.Length > 0
                ? configuredUrl_
                : await BridgeLocator.DiscoverAsync(probe_, logger_, ct).ConfigureAwait(false);
            lastResolveUtc_ = DateTime.UtcNow;
            if (url is null)
            {
                logger_.LogWarning("No bridge found; the app will keep looking.");
                return;
            }

            SetBaseAddress(url);
            logger_.LogInformation("Bridge resolved at {Url}.", url);
        }
        finally
        {
            gate_.Release();
        }
    }

    private async Task ResetAddressAsync(CancellationToken ct)
    {
        await gate_.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            client_?.Dispose();
            client_ = null;
            baseUrl_ = null;
        }
        finally
        {
            gate_.Release();
        }
    }

    private void SetBaseAddress(string url)
    {
        url = url.TrimEnd('/');
        client_?.Dispose();
        client_ = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
        {
            BaseAddress = new Uri(url + "/"),
            Timeout = CallTimeout
        };
        baseUrl_ = url;
    }

    private async Task<ForwardedResponse?> TryForwardAsync(HttpMethod method, string path, string? body, string? contentType, CancellationToken ct)
    {
        HttpClient? client = client_;
        if (client is null)
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(method, path.TrimStart('/'));
            if (body is not null)
            {
                // The client's own Content-Type goes through as-is: StringContent's mediaType
                // argument rejects one that already carries a charset ("application/json;
                // charset=utf-8"), which browsers send.
                var content = new StringContent(body, Encoding.UTF8);
                content.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed)
                    ? parsed
                    : MediaTypeHeaderValue.Parse("application/json");
                request.Content = content;
            }

            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            Reachable = true;
            return new ForwardedResponse(
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8",
                bytes);
        }
        catch (Exception e) when (!ct.IsCancellationRequested
            && (e is HttpRequestException or OperationCanceledException or ObjectDisposedException))
        {
            Reachable = false;
            logger_.LogDebug(e, "Bridge call {Method} {Path} failed.", method, path);
            return null;
        }
    }
}
