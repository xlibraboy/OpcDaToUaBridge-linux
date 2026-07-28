using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.Services;

public sealed class BridgeConnectionManager : IAsyncDisposable
{
    private readonly Dictionary<string, BridgeSession> sessions_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync_ = new();

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

    public async Task ConnectAllAsync(HmiClientConfig config, CancellationToken ct)
    {
        await DisconnectAllAsync().ConfigureAwait(false);

        foreach (HmiBridgeEndpoint bridge in config.EnabledBridges())
        {
            ct.ThrowIfCancellationRequested();
            await ConnectBridgeAsync(bridge, ct).ConfigureAwait(false);
        }

        CacheChanged?.Invoke();
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

    public async Task DisconnectAllAsync()
    {
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

        public async ValueTask DisposeAsync()
        {
            await Hub.DisposeAsync().ConfigureAwait(false);
            Api.Dispose();
        }
    }
}
