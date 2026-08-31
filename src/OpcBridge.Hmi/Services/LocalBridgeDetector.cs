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

    /// <summary>Returns the first candidate URL that answers, or null.</summary>
    public static async Task<string?> DetectAsync(
        string[]? candidates = null,
        int timeoutMs = 1500,
        CancellationToken ct = default)
    {
        candidates ??= DefaultCandidates;
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
}
