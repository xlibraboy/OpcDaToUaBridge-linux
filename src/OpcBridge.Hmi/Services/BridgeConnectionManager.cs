using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.Services;

public sealed class BridgeConnectionManager : IAsyncDisposable
{
    private readonly Dictionary<string, BridgeSession> sessions_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync_ = new();
    private System.Threading.Timer? influxTimer_;

    /// <summary>How often each bridge's InfluxDB writer state is re-polled.</summary>
    private static readonly TimeSpan InfluxPollInterval = TimeSpan.FromSeconds(5);

    public MultiBridgeTagCache Cache { get; } = new();

    public IReadOnlyCollection<string> ConnectedBridgeIds
    {
        get
        {
            lock (sync_)
            {
                return sessions_.Keys.ToArray();
            }
        }
    }

    public event Func<string, Task>? BridgeReconnected;
    public event Func<string, HmiMappingsChanged, Task>? MappingsChanged;
    public event Action? CacheChanged;

    /// <summary>Raised when a bridge's live InfluxDB connection state changes (bridgeId, connected).</summary>
    public event Action<string, bool>? InfluxStatusChanged;

    public async Task ConnectAllAsync(HmiClientConfig config, CancellationToken ct)
    {
        await DisconnectAllAsync().ConfigureAwait(false);

        foreach (HmiBridgeEndpoint bridge in config.EnabledBridges())
        {
            ct.ThrowIfCancellationRequested();
            await ConnectBridgeAsync(bridge, ct).ConfigureAwait(false);
        }

        CacheChanged?.Invoke();
        StartInfluxPolling();
    }

    private void StartInfluxPolling()
    {
        lock (sync_)
        {
            influxTimer_?.Dispose();
            influxTimer_ = new System.Threading.Timer(
                _ => PollInfluxStatusAsync(),
                null,
                TimeSpan.FromSeconds(1),
                InfluxPollInterval);
        }
    }

    private async void PollInfluxStatusAsync()
    {
        string[] bridgeIds;
        lock (sync_)
        {
            bridgeIds = sessions_.Keys.ToArray();
        }

        foreach (string bridgeId in bridgeIds)
        {
            try
            {
                await RefreshInfluxStatusAsync(bridgeId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // transient poll failure: leave the last known state as-is
            }
        }
    }

    public async Task ConnectBridgeAsync(HmiBridgeEndpoint bridge, CancellationToken ct)
    {
        string id = bridge.Id.Trim();
        BridgeApiClient api = new();
        api.SetBaseAddress(bridge.BaseUrl);
        HmiHubClient hub = new();

        HmiTagsResponse snapshot = await api.GetTagsAsync(ct).ConfigureAwait(false);
        Cache.ReplaceBridge(id, snapshot.Tags);

        await hub.ConnectAsync(
            bridge.BaseUrl,
            batch =>
            {
                Cache.ApplyDeltas(id, batch);
                CacheChanged?.Invoke();
                return Task.CompletedTask;
            },
            msg =>
            {
                Func<string, HmiMappingsChanged, Task>? handler = MappingsChanged;
                return handler is null ? Task.CompletedTask : handler(id, msg);
            },
            ct).ConfigureAwait(false);

        hub.Reconnected += async _ =>
        {
            try
            {
                HmiTagsResponse refresh = await api.GetTagsAsync(CancellationToken.None).ConfigureAwait(false);
                Cache.ReplaceBridge(id, refresh.Tags);
                CacheChanged?.Invoke();
                await RefreshInfluxStatusAsync(id, CancellationToken.None).ConfigureAwait(false);
                Func<string, Task>? reconnected = BridgeReconnected;
                if (reconnected is not null)
                {
                    await reconnected(id).ConfigureAwait(false);
                }
            }
            catch
            {
                // leave cache as-is on refresh failure
            }
        };

        lock (sync_)
        {
            sessions_[id] = new BridgeSession(id, bridge.BaseUrl, api, hub, snapshot.Version);
        }
    }

    public async Task RefreshBridgeSnapshotAsync(string bridgeId, CancellationToken ct)
    {
        BridgeSession? session;
        lock (sync_)
        {
            sessions_.TryGetValue(bridgeId, out session);
        }

        if (session is null)
        {
            return;
        }

        HmiTagsResponse snapshot = await session.Api.GetTagsAsync(ct).ConfigureAwait(false);
        session.MappingVersion = snapshot.Version;
        Cache.ReplaceBridge(session.BridgeId, snapshot.Tags);
        CacheChanged?.Invoke();
    }

    public bool TryGetSession(string bridgeId, out BridgeSession? session)
    {
        lock (sync_)
        {
            if (sessions_.TryGetValue(bridgeId, out BridgeSession? found))
            {
                session = found;
                return true;
            }

            session = null;
            return false;
        }
    }

    /// <summary>
    /// Re-queries a bridge's InfluxDB writer state and raises <see cref="InfluxStatusChanged"/>
    /// only when the connected state actually changed.
    /// </summary>
    public async Task RefreshInfluxStatusAsync(string bridgeId, CancellationToken ct)
    {
        BridgeSession? session;
        lock (sync_)
        {
            sessions_.TryGetValue(bridgeId, out session);
        }

        if (session is null)
        {
            return;
        }

        HmiInfluxStatus? status = await session.Api.GetInfluxStatusAsync(ct).ConfigureAwait(false);
        bool connected = status?.IsConnected == true;
        bool changed;
        lock (sync_)
        {
            changed = session.InfluxConnected != connected;
            session.InfluxConnected = connected;
        }

        if (changed)
        {
            InfluxStatusChanged?.Invoke(session.BridgeId, connected);
        }
    }

    public async Task DisconnectAllAsync()
    {
        lock (sync_)
        {
            influxTimer_?.Dispose();
            influxTimer_ = null;
        }

        BridgeSession[] copy;
        lock (sync_)
        {
            copy = sessions_.Values.ToArray();
            sessions_.Clear();
        }

        foreach (BridgeSession session in copy)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        Cache.Clear();
        CacheChanged?.Invoke();
    }

    public async ValueTask DisposeAsync() => await DisconnectAllAsync().ConfigureAwait(false);

    public sealed class BridgeSession : IAsyncDisposable
    {
        public BridgeSession(string bridgeId, string baseUrl, BridgeApiClient api, HmiHubClient hub, long mappingVersion)
        {
            BridgeId = bridgeId;
            BaseUrl = baseUrl;
            Api = api;
            Hub = hub;
            MappingVersion = mappingVersion;
        }

        public string BridgeId { get; }
        public string BaseUrl { get; }
        public BridgeApiClient Api { get; }
        public HmiHubClient Hub { get; }
        public long MappingVersion { get; set; }

        /// <summary>Last known live InfluxDB writer connection state for this bridge.</summary>
        public bool InfluxConnected { get; set; }

        public async ValueTask DisposeAsync()
        {
            await Hub.DisposeAsync().ConfigureAwait(false);
            Api.Dispose();
        }
    }
}
