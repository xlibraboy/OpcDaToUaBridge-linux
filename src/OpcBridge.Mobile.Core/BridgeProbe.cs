namespace OpcBridge.Mobile.Core;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpcBridge.Core;

/// <summary>
/// Finds a bridge on the plant LAN from the host or address the operator typed: probes the
/// known container publish port, then the bridge's HTTP block (8080–8180 by default).
/// </summary>
public static class BridgeProbe
{
    /// <summary>
    /// The block swept after the typed and known ports. Defaults to the bridge's standard
    /// 8080–8180; <c>OPCBRIDGE_HTTP_SCAN_RANGE</c> lets a desktop-hosted runtime follow an
    /// install pinned elsewhere (not settable on Android, where the typed port is used).
    /// </summary>
    public static PortRange ScanRange =>
        PortHelper.ScanRangeFromEnvironment("OPCBRIDGE_HTTP_SCAN_RANGE", PortRange.DefaultHttp);

    /// <summary>
    /// Per-port timeout while sweeping the range: a bridge that is listening answers in
    /// milliseconds, so a host that silently drops packets cannot hold the Add button for
    /// the two minutes a full 1.2 s-per-port sweep would take.
    /// </summary>
    public const int ScanTimeoutMs = 400;

    /// <summary>
    /// How long the whole sweep may take. A host that silently drops packets must not hold
    /// the Add button through a hundred per-port timeouts; the operator can retry.
    /// </summary>
    public const int SearchBudgetMs = 8000;

    /// <summary>Host ports the bridge is commonly published on when it runs in a container.</summary>
    public static readonly int[] KnownHostPorts = { 18080 };

    /// <summary>The first address on <paramref name="host"/> that answers as a bridge, or null.</summary>
    public static async Task<string?> FindAsync(
        string? host,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default,
        int searchBudgetMs = SearchBudgetMs)
    {
        (string name, int? explicitPort) = ParseHost(host);
        if (name.Length == 0)
        {
            return null;
        }

        if (searchBudgetMs <= 0)
        {
            return null;
        }

        using CancellationTokenSource budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(searchBudgetMs);
        try
        {
            Stopwatch budget = Stopwatch.StartNew();
            foreach (int port in CandidatePorts(explicitPort))
            {
                budgetCts.Token.ThrowIfCancellationRequested();
                int remaining = searchBudgetMs - (int)budget.ElapsedMilliseconds;
                if (remaining <= 0)
                {
                    break;
                }

                string baseUrl = $"http://{name}:{port}";
                // The port the operator typed, and the known container publish ports, get the
                // caller's full timeout; the wide sweep gets the short one. Neither may outlast
                // the whole search budget, and the budget token cancels a probe already inside
                // a connection attempt.
                int perPortTimeout = port == explicitPort || KnownHostPorts.Contains(port)
                    ? timeoutMs
                    : Math.Min(timeoutMs, ScanTimeoutMs);
                perPortTimeout = Math.Min(perPortTimeout, remaining);
                if (await IsBridgeAsync(baseUrl, perPortTimeout, budgetCts.Token).ConfigureAwait(false))
                {
                    return baseUrl;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The search budget, not the operator, ended this sweep.
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
        // The operator cancelling the search is not a failed probe: let it through so the
        // page can stop the sweep instead of walking every remaining port.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // A failed probe is an answer (not a bridge), never an exception: Android's HTTP handler
        // reports a socket closed before the response arrives as WebException, while the desktop
        // transport wraps it as HttpRequestException. Both — and an I/O or socket failure — mean
        // the address is not a reachable bridge, so the sweep moves on instead of failing the add.
        catch (Exception ex) when (
            ex is HttpRequestException or WebException or IOException or SocketException
                or TaskCanceledException or OperationCanceledException or JsonException)
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

        PortRange scanRange = ScanRange;
        for (int port = scanRange.Start; port <= scanRange.End; port++)
        {
            if (seen.Add(port))
            {
                yield return port;
            }
        }
    }
}
