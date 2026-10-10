using System.Text.Json;
using OpcBridge.Core;

namespace OpcBridge.Logic;

/// <summary>
/// Finds the bridge's HTTP address when none is configured: the bridge's own default port
/// first, then a sweep of the port range for the identity probe every bridge answers
/// anonymously. The lowest answering port wins, so a host running several bridges resolves
/// the same one every time. Ported from the phone's BridgeProbe so both clients agree on
/// what a bridge looks like.
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
        List<int> ports = candidates.Distinct().OrderBy(port => port).ToList();

        // The default port then the container publish port, in order: on a host with one
        // bridge this is one short request, and the default always beats an auto-rolled port.
        foreach (int port in ports.Where(port => port is PortHelper.HttpScanStart or KnownContainerPort))
        {
            string url = "http://127.0.0.1:" + port;
            if (await LooksLikeBridgeAsync(probe, url, ct).ConfigureAwait(false))
            {
                logger.LogInformation("Found a bridge at {Url}.", url);
                return url;
            }
        }

        // The rest of the range is swept in parallel; the lowest responder wins rather than
        // the fastest one, because a dev/test host can run several bridges at once and
        // "whichever probe returned first" resolved a different bridge from run to run.
        int foundPort = int.MaxValue;
        await Parallel.ForEachAsync(
            ports.Where(port => port is not PortHelper.HttpScanStart and not KnownContainerPort),
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
            async (port, token) =>
            {
                if (port >= Volatile.Read(ref foundPort))
                {
                    return;
                }

                if (await LooksLikeBridgeAsync(probe, "http://127.0.0.1:" + port, token).ConfigureAwait(false))
                {
                    int current = Volatile.Read(ref foundPort);
                    while (port < current)
                    {
                        int prior = Interlocked.CompareExchange(ref foundPort, port, current);
                        if (prior == current)
                        {
                            break;
                        }

                        current = prior;
                    }
                }
            }).ConfigureAwait(false);

        if (foundPort == int.MaxValue)
        {
            return null;
        }

        string foundUrl = "http://127.0.0.1:" + foundPort;
        logger.LogInformation("Found a bridge at {Url}.", foundUrl);
        return foundUrl;
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
