using System.Text.Json;

namespace OpcBridge.Hmi.Services;

/// <summary>
/// Discovers the bridge HTTP port at runtime. The bridge auto-assigns a
/// non-default port when 8080 is already in use; this helper finds whichever
/// port the bridge is actually listening on by probing /api/status/ports.
/// </summary>
public static class BridgePortDiscovery
{
    public const int DefaultPort = 8080;
    public const int ScanStart = 8080;
    public const int ScanEnd = 8180;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Returns the bridge HTTP base URL (e.g. "http://127.0.0.1:8082"),
    /// or the configured URL unchanged when the bridge cannot be reached.
    /// </summary>
    public static async Task<string> DiscoverBaseUrlAsync(
        string configuredBaseUrl,
        CancellationToken cancellationToken)
    {
        string host = "127.0.0.1";
        try
        {
            if (Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out Uri? configured))
            {
                host = configured.Host;
            }
        }
        catch
        {
            // keep default host
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };

        // 1. Try the configured URL first — fast path when the bridge is already reachable there.
        //    Keep the URL as configured: the app's self-reported port can differ from the
        //    externally reachable one (e.g. behind a container port-publish), so rewriting it
        //    would point at a port where nothing answers. Only follow a port move when the
        //    configured URL is dead (scan path below).
        if (Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out Uri? first) &&
            await TryGetHttpPortAsync(client, first, cancellationToken).ConfigureAwait(false) is not null)
        {
            return configuredBaseUrl.TrimEnd('/');
        }

        // 2. Scan the range on the same host until a bridge answers.
        for (int port = ScanStart; port <= ScanEnd; port++)
        {
            if (port == first?.Port)
            {
                continue; // already probed
            }

            var candidate = new UriBuilder("http", host, port).Uri;
            if (await TryGetHttpPortAsync(client, candidate, cancellationToken).ConfigureAwait(false) is { } foundPort)
            {
                return BuildBaseUrl(candidate, foundPort);
            }
        }

        // 3. Bridge unreachable — leave the configured URL so the user can retry manually.
        return configuredBaseUrl;
    }

    private static string BuildBaseUrl(Uri probeBase, int httpPort)
    {
        var builder = new UriBuilder(probeBase.Scheme, probeBase.Host, httpPort);
        return builder.Uri.ToString().TrimEnd('/');
    }

    private static async Task<int?> TryGetHttpPortAsync(
        HttpClient client,
        Uri baseUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = new UriBuilder(baseUrl) { Path = "/api/status/ports" }.Uri;
            using HttpResponseMessage response = await client
                .GetAsync(url, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("httpPort", out JsonElement port) &&
                port.ValueKind == JsonValueKind.Number &&
                port.TryGetInt32(out int httpPort) &&
                httpPort > 0)
            {
                return httpPort;
            }
        }
        catch
        {
            // probe failed — try next candidate
        }

        return null;
    }
}
