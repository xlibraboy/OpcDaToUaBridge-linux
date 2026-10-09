namespace OpcBridge.Mobile.Core;

using System.Text.Json;

/// <summary>
/// Finds a bridge on the plant LAN from the host or address the operator typed: probes the
/// known container publish port, then the bridge's own HTTP range (8080–8180).
/// </summary>
public static class BridgeProbe
{
    public const int ScanStart = 8080;
    public const int ScanEnd = 8180;

    /// <summary>
    /// Per-port timeout while sweeping the range: a bridge that is listening answers in
    /// milliseconds, so a host that silently drops packets cannot hold the Add button for
    /// the two minutes a full 1.2 s-per-port sweep would take.
    /// </summary>
    public const int ScanTimeoutMs = 400;

    /// <summary>Host ports the bridge is commonly published on when it runs in a container.</summary>
    public static readonly int[] KnownHostPorts = { 18080 };

    /// <summary>The first address on <paramref name="host"/> that answers as a bridge, or null.</summary>
    public static async Task<string?> FindAsync(string? host, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        (string name, int? explicitPort) = ParseHost(host);
        if (name.Length == 0)
        {
            return null;
        }

        foreach (int port in CandidatePorts(explicitPort))
        {
            string baseUrl = $"http://{name}:{port}";
            // The port the operator typed, and the known container publish ports, get the
            // caller's full timeout; the wide sweep gets the short one.
            int perPortTimeout = port == explicitPort || KnownHostPorts.Contains(port)
                ? timeoutMs
                : Math.Min(timeoutMs, ScanTimeoutMs);
            if (await IsBridgeAsync(baseUrl, perPortTimeout, cancellationToken).ConfigureAwait(false))
            {
                return baseUrl;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the address answers as a bridge. The probe is <c>/api/auth/me</c>, which stays
    /// anonymous whether or not Auth is enabled — <c>/api/status/ports</c>, the desktop HMI's
    /// same-origin probe, is session-gated and so would reject a phone that has no session yet.
    /// The body must carry the endpoint's <c>authEnabled</c> field, so a foreign web server on
    /// the same port is not mistaken for a bridge. The body is read as a string (bounded by
    /// Content-Length) rather than parsed off the stream: a keep-alive connection never reaches
    /// EOF, and a stream parse would sit there until the timeout on a real bridge.
    /// </summary>
    public static async Task<bool> IsBridgeAsync(string? baseUrl, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using HttpResponseMessage response = await http
                .GetAsync(baseUrl.TrimEnd('/') + "/api/auth/me", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("authEnabled", out _);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Strips a scheme, path and port from whatever the operator typed; returns the host.</summary>
    public static (string Host, int? Port) ParseHost(string? host)
    {
        string value = (host ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return (string.Empty, null);
        }

        if (value.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed))
        {
            int? port = parsed.IsDefaultPort ? null : parsed.Port;
            return (parsed.Host, port);
        }

        string[] parts = value.TrimEnd('/').Split(':');
        if (parts.Length == 2 && int.TryParse(parts[1], out int plainPort) && plainPort is > 0 and < 65536)
        {
            return (parts[0], plainPort);
        }

        return (value.TrimEnd('/'), null);
    }

    private static IEnumerable<int> CandidatePorts(int? explicitPort)
    {
        HashSet<int> seen = new();
        if (explicitPort is int first && seen.Add(first))
        {
            yield return first;
        }

        foreach (int port in KnownHostPorts)
        {
            if (seen.Add(port))
            {
                yield return port;
            }
        }

        for (int port = ScanStart; port <= ScanEnd; port++)
        {
            if (seen.Add(port))
            {
                yield return port;
            }
        }
    }
}
