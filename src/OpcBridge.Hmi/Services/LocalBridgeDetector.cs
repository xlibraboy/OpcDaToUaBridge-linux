using System.Net.Http;

namespace OpcBridge.Hmi.Services;

/// <summary>
/// Detects a locally installed OpcBridge by probing the cheap HMI displays
/// endpoint on the standard local addresses. Pure probe — never mutates state.
/// </summary>
public static class LocalBridgeDetector
{
    public static readonly string[] DefaultCandidates =
    [
        "http://127.0.0.1:8080",
        "http://localhost:8080"
    ];

    /// <summary>
    /// Host ports the bridge is commonly published on when it runs in a container
    /// (e.g. <c>docker run -p 18080:8080</c>). A published host port differs from the
    /// bridge's self-reported one, so these can never be found by scanning the bridge's
    /// own 8080-8180 range.
    /// </summary>
    public static readonly int[] KnownHostPorts =
    [
        18080
    ];

    /// <summary>Returns the first candidate URL that answers, or null.</summary>
    public static async Task<string?> DetectAsync(
        string[]? candidates = null,
        int timeoutMs = 1500,
        CancellationToken ct = default)
    {
        candidates ??= BuildDefaultCandidates();
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        foreach (string url in candidates)
        {
            try
            {
                using HttpResponseMessage response = await http
                    .GetAsync(url.TrimEnd('/') + "/api/hmi/displays", ct)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return url;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                // try next candidate
            }
        }

        return null;
    }

    /// <summary>
    /// Default probe list: the standard local URLs, then the documented container publish
    /// ports, then the bridge's own HTTP scan range. Known ports come before the range
    /// because a published bridge is not on 8080-8180, and the range can contain ports that
    /// accept a connection but never answer (each costing a full timeout).
    /// </summary>
    public static string[] BuildDefaultCandidates()
    {
        List<string> candidates = new(DefaultCandidates);
        HashSet<string> seen = new(candidates, StringComparer.OrdinalIgnoreCase);

        foreach (int port in KnownHostPorts)
        {
            string url = $"http://127.0.0.1:{port}";
            if (seen.Add(url))
            {
                candidates.Add(url);
            }
        }

        for (int port = BridgePortDiscovery.ScanStart; port <= BridgePortDiscovery.ScanEnd; port++)
        {
            string url = $"http://127.0.0.1:{port}";
            if (seen.Add(url))
            {
                candidates.Add(url);
            }
        }

        return candidates.ToArray();
    }
}
