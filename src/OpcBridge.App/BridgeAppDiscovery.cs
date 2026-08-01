using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpcBridge.App;

public sealed record BridgeAppPresence(
    string MachineName,
    string ProbeHost,
    bool IsLocal,
    string Version,
    string InformationalVersion);

public sealed record BridgeAppFleetStatus(
    int DetectedCount,
    DateTime? LastRefreshUtc,
    IReadOnlyList<BridgeAppPresence> DetectedApps)
{
    public static BridgeAppFleetStatus Empty { get; } = new(0, null, Array.Empty<BridgeAppPresence>());
}

internal sealed class BridgeAppDiscovery : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private readonly int _httpPort = 8080;
    private readonly DaRuntimeSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BridgeAppDiscovery> _logger;
    private readonly object _lock = new();
    private BridgeAppFleetStatus _status = BridgeAppFleetStatus.Empty;

    public BridgeAppDiscovery(
        DaRuntimeSettings settings,
        IHttpClientFactory httpClientFactory,
        ILogger<BridgeAppDiscovery> logger,
        int httpPort = 8080)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _httpPort = httpPort;
    }

    public BridgeAppFleetStatus GetStatus()
    {
        lock (_lock)
        {
            return _status;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Seed with local app immediately
        UpdateStatus(BuildLocalOnly());

        // Run first refresh immediately
        await RefreshAsync(stoppingToken).ConfigureAwait(false);

        using PeriodicTimer timer = new(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RefreshAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = _settings.GetSnapshot();
            var candidates = BuildCandidateHosts(snapshot.Sources);
            var detected = await ProbeHostsAsync(candidates, cancellationToken).ConfigureAwait(false);

            var status = new BridgeAppFleetStatus(
                detected.Count,
                DateTime.UtcNow,
                detected);

            UpdateStatus(status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bridge app discovery refresh failed");
        }
    }

    private void UpdateStatus(BridgeAppFleetStatus status)
    {
        lock (_lock)
        {
            _status = status;
        }
    }

    private BridgeAppFleetStatus BuildLocalOnly()
    {
        var local = AppInfoSnapshot.CreateCurrent();
        var presence = new BridgeAppPresence(
            local.MachineName,
            local.MachineName,
            true,
            local.Version,
            local.InformationalVersion);
        return new BridgeAppFleetStatus(1, null, new[] { presence });
    }

    private List<string> BuildCandidateHosts(IReadOnlyList<DaSourceRuntimeSettings> sources)
    {
        var localMachine = Environment.MachineName;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Always include local machine
        candidates.Add(localMachine);

        // Add all source hosts
        foreach (var source in sources)
        {
            if (!string.IsNullOrWhiteSpace(source.Host))
            {
                candidates.Add(source.Host.Trim());
            }
        }

        return candidates.ToList();
    }

    private async Task<List<BridgeAppPresence>> ProbeHostsAsync(
        List<string> candidates,
        CancellationToken cancellationToken)
    {
        var localMachine = Environment.MachineName;
        var results = new Dictionary<string, BridgeAppPresence>(StringComparer.OrdinalIgnoreCase);

        // Probe all hosts in parallel
        var tasks = candidates.Select(async host =>
        {
            if (IsLocalHost(host, localMachine))
            {
                // Local app - no HTTP needed
                var local = AppInfoSnapshot.CreateCurrent();
                return new BridgeAppPresence(
                    local.MachineName,
                    local.MachineName,
                    true,
                    local.Version,
                    local.InformationalVersion);
            }

            // Remote host - probe via HTTP
            return await ProbeRemoteHostAsync(host, cancellationToken).ConfigureAwait(false);
        });

        var detected = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Deduplicate by machineName (case-insensitive)
        foreach (var presence in detected)
        {
            if (presence != null && !results.ContainsKey(presence.MachineName))
            {
                results[presence.MachineName] = presence;
            }
        }

        // Sort: local first, then by machine name
        return results.Values
            .OrderBy(p => p.IsLocal ? 0 : 1)
            .ThenBy(p => p.MachineName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<BridgeAppPresence?> ProbeRemoteHostAsync(
        string host,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("BridgeAppDiscovery");
            var baseUrl = $"http://{host}:{_httpPort}";

            // Step 1: Check /health
            using var healthCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            healthCts.CancelAfter(ProbeTimeout);

             using var healthResponse = await client.GetAsync($"{baseUrl}/health", healthCts.Token).ConfigureAwait(false);
            if (healthResponse.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            var healthJson = await healthResponse.Content.ReadFromJsonAsync<JsonElement>(healthCts.Token).ConfigureAwait(false);
            if (healthJson.GetProperty("status").GetString() != "ok")
            {
                return null;
            }

            // Step 2: Get /api/app-info
            using var appInfoCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            appInfoCts.CancelAfter(ProbeTimeout);

             using var appInfoResponse = await client.GetAsync($"{baseUrl}/api/app-info", appInfoCts.Token).ConfigureAwait(false);
            if (appInfoResponse.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            var appInfo = await appInfoResponse.Content.ReadFromJsonAsync<AppInfoSnapshot>(appInfoCts.Token).ConfigureAwait(false);
            if (appInfo == null || appInfo.Name != "OpcBridge.App" || string.IsNullOrWhiteSpace(appInfo.MachineName))
            {
                return null;
            }

            return new BridgeAppPresence(
                appInfo.MachineName,
                host,
                false,
                appInfo.Version,
                appInfo.InformationalVersion);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to probe remote host {Host}", host);
            return null;
        }
    }

    private static bool IsLocalHost(string host, string localMachine)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;

        var trimmed = host.Trim();
        return trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(".", StringComparison.Ordinal)
            || trimmed.Equals("127.0.0.1", StringComparison.Ordinal)
            || trimmed.Equals("::1", StringComparison.Ordinal)
            || trimmed.Equals(localMachine, StringComparison.OrdinalIgnoreCase);
    }
}
