using System.Text.Json;
using OpcBridge.Core;

namespace OpcBridge.Logic;

/// <summary>
/// Finds the bridge's HTTP address when none is configured: a sweep of the bridge's port
/// range for the identity probe every bridge answers anonymously. Ported from the phone's
/// BridgeProbe so both clients agree on what a bridge looks like.
/// </summary>
internal static class BridgeLocator
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(400);

    /// <summary>The container host port the phone also probes; hosts publish the bridge there.</summary>
    private const int KnownContainerPort = 18080;

    public static Task<string?> DiscoverAsync(HttpClient probe, ILogger logger, CancellationToken ct) =>
        DiscoverAsync(probe, logger, CandidatePorts(), ct);

    /// <summary>The candidate list is injectable so tests can pin the sweep to their fake bridge.</summary>
    internal static async Task<string?> DiscoverAsync(HttpClient probe, ILogger logger, IEnumerable<int> candidates, CancellationToken ct)
    {
        string? found = null;
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
            async (port, token) =>
            {
                if (Volatile.Read(ref found) is not null)
                {
                    return;
                }

                string url = "http://127.0.0.1:" + port;
                if (await LooksLikeBridgeAsync(probe, url, token).ConfigureAwait(false))
                {
                    Interlocked.CompareExchange(ref found, url, null);
                }
            }).ConfigureAwait(false);

        if (found is not null)
        {
            logger.LogInformation("Found a bridge at {Url}.", found);
        }

        return found;
    }

    private static IEnumerable<int> CandidatePorts()
    {
        yield return PortHelper.HttpScanStart;
        yield return KnownContainerPort;
        for (int port = PortHelper.HttpScanStart + 1; port <= PortHelper.HttpScanEnd; port++)
        {
            yield return port;
        }
    }

    private static async Task<bool> LooksLikeBridgeAsync(HttpClient probe, string baseUrl, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using HttpResponseMessage response = await probe.GetAsync(baseUrl + "/api/auth/me", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("authEnabled", out _);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            return false;
        }
    }
}
