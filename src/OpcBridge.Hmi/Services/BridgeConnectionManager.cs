using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.Services;

/// <summary>Outcome of one connect pass across every configured bridge.</summary>
public sealed record ConnectAllResult(
    IReadOnlyCollection<string> Connected,
    IReadOnlyList<BridgeLinkStatus> Failed);

/// <summary>Cadences the connection manager runs on; tests pass fast values.</summary>
public sealed record BridgeManagerOptions(
    TimeSpan PollInterval,
    IReadOnlyList<TimeSpan> SnapshotRetryDelays,
    IReadOnlyList<TimeSpan> BridgeRetryDelays,
    HmiHubTiming HubTiming)
{
    public static BridgeManagerOptions Default { get; } = new(
        TimeSpan.FromSeconds(5),
        new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8)
        },
        new[]
        {
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        },
        HmiHubTiming.Default);
}

public sealed class BridgeConnectionManager : IAsyncDisposable
{
    private readonly BridgeManagerOptions options_;
    private readonly Dictionary<string, BridgeSession> sessions_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync_ = new();
    private System.Threading.Timer? housekeepingTimer_;
    private int ticking_;

    public BridgeConnectionManager()
        : this(BridgeManagerOptions.Default)
    {
    }

    public BridgeConnectionManager(BridgeManagerOptions options)
    {
        options_ = options;
    }

    public MultiBridgeTagCache Cache { get; } = new();

    /// <summary>Ids of the bridges whose link is live right now.</summary>
    public IReadOnlyCollection<string> ConnectedBridgeIds
    {
        get
        {
            lock (sync_)
            {
                return sessions_.Values
                    .Where(session => session.LinkState == BridgeLinkState.Connected)
                    .Select(session => session.BridgeId)
                    .ToArray();
            }
        }
    }

    /// <summary>Live state of every session — connected, reconnecting or failed — for the status surfaces.</summary>
    public IReadOnlyList<BridgeLinkStatus> LinkStatuses
    {
        get
        {
            lock (sync_)
            {
                return sessions_.Values
                    .Select(session => new BridgeLinkStatus(session.BridgeId, session.LinkState, session.LastError))
                    .ToArray();
            }
        }
    }

    public event Func<string, Task>? BridgeReconnected;
    public event Func<string, HmiMappingsChanged, Task>? MappingsChanged;
    public event Action? CacheChanged;

    /// <summary>Raised when a bridge's live link state changes (bridgeId, state, error).</summary>
    public event Action<string, BridgeLinkState, string?>? LinkStateChanged;

    /// <summary>Raised when a bridge's live InfluxDB connection state changes (bridgeId, connected).</summary>
    public event Action<string, bool>? InfluxStatusChanged;

    /// <summary>
    /// Connects every enabled bridge independently: one unreachable bridge no longer aborts the
    /// healthy ones — it stays in the session list as <see cref="BridgeLinkState.Failed"/> and the
    /// housekeeping tick keeps retrying it. Throws only when no bridge at all could be reached,
    /// so a plain connect failure still reports like one.
    /// </summary>
    public async Task<ConnectAllResult> ConnectAllAsync(HmiClientConfig config, CancellationToken ct)
    {
        await DisconnectAllAsync().ConfigureAwait(false);

        List<HmiBridgeEndpoint> endpoints = config.EnabledBridges().ToList();
        if (endpoints.Count == 0)
        {
            return new ConnectAllResult(Array.Empty<string>(), Array.Empty<BridgeLinkStatus>());
        }

        List<BridgeLinkStatus> failed = new();
        foreach (HmiBridgeEndpoint bridge in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ConnectBridgeAsync(bridge, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed.Add(new BridgeLinkStatus(bridge.Id.Trim(), BridgeLinkState.Failed, ex.Message));
            }
        }

        if (failed.Count == endpoints.Count)
        {
            await DisconnectAllAsync().ConfigureAwait(false);
            throw new InvalidOperationException(failed[0].Error ?? "Connect failed");
        }

        CacheChanged?.Invoke();
        StartHousekeeping();
        return new ConnectAllResult(ConnectedBridgeIds, failed);
    }

    /// <summary>
    /// Connects one bridge (or reconnects a failed one): REST snapshot first, then the live hub.
    /// A failure leaves the session in the list, endpoint kept, as
    /// <see cref="BridgeLinkState.Failed"/> with a retry scheduled.
    /// </summary>
    public async Task ConnectBridgeAsync(HmiBridgeEndpoint bridge, CancellationToken ct)
    {
        BridgeSession session = GetOrCreateSession(bridge);
        await ConnectSessionCoreAsync(session, ct).ConfigureAwait(false);
    }

    private BridgeSession GetOrCreateSession(HmiBridgeEndpoint bridge)
    {
        string id = bridge.Id.Trim();
        lock (sync_)
        {
            if (sessions_.TryGetValue(id, out BridgeSession? existing) && !existing.Retired)
            {
                return existing;
            }

            var session = new BridgeSession(id, bridge);
            sessions_[id] = session;
            return session;
        }
    }

    private async Task ConnectSessionCoreAsync(BridgeSession session, CancellationToken ct)
    {
        await ResetHubAsync(session).ConfigureAwait(false);
        try
        {
            HmiTagsResponse snapshot = await session.Api.GetTagsAsync(ct).ConfigureAwait(false);
            Cache.ReplaceBridge(session.BridgeId, snapshot.Tags);
            session.MappingVersion = snapshot.Version;
            CacheChanged?.Invoke();

            var hub = new HmiHubClient();
            session.Hub = hub;
            await hub.ConnectAsync(
                session.Endpoint.BaseUrl,
                batch =>
                {
                    Cache.ApplyDeltas(session.BridgeId, batch);
                    CacheChanged?.Invoke();
                    return Task.CompletedTask;
                },
                msg =>
                {
                    Func<string, HmiMappingsChanged, Task>? handler = MappingsChanged;
                    return handler is null ? Task.CompletedTask : handler(session.BridgeId, msg);
                },
                ct,
                options_.HubTiming).ConfigureAwait(false);

            hub.Reconnecting += error => HandleReconnectingAsync(session, error);
            hub.Reconnected += _ => HandleReconnectedAsync(session);
            hub.Closed += error => HandleClosedAsync(session, error);

            lock (sync_)
            {
                session.RetryCount = 0;
                session.NextRetryUtc = DateTime.MinValue;
                session.NeedsResync = false;
            }

            SetLinkState(session, BridgeLinkState.Connected, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ResetHubAsync(session).ConfigureAwait(false);
            SetLinkState(session, BridgeLinkState.Failed, ex.Message);
            ScheduleRetry(session);
            throw;
        }
    }

    private async Task ResetHubAsync(BridgeSession session)
    {
        HmiHubClient? hub;
        lock (sync_)
        {
            hub = session.Hub;
            session.Hub = null;
        }

        if (hub is not null)
        {
            await hub.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-labels live bridges: every renamed bridge's old session is torn down first, then each
    /// reconnects under its new id. The two phases keep a rename from colliding with a live id
    /// another rename is about to vacate (swapped or adopted names), which a per-bridge
    /// disconnect-then-reconnect would tear down again.
    /// </summary>
    public async Task RenameBridgesAsync(IReadOnlyList<(string OldId, HmiBridgeEndpoint Bridge)> renames)
    {
        if (renames.Count == 0)
        {
            return;
        }

        List<BridgeSession> retired = new();
        lock (sync_)
        {
            foreach ((string oldId, _) in renames)
            {
                if (sessions_.Remove(oldId, out BridgeSession? session) && session is not null)
                {
                    retired.Add(session);
                }
            }
        }

        foreach (BridgeSession session in retired)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        foreach ((string oldId, _) in renames)
        {
            Cache.ClearBridge(oldId);
        }

        CacheChanged?.Invoke();

        foreach ((_, HmiBridgeEndpoint bridge) in renames)
        {
            // A renamed bridge that cannot be reached right now follows the same rule as a
            // failed connect: its session stays Failed with a retry, it never throws the rename.
            try
            {
                await ConnectBridgeAsync(bridge, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // the failed session carries the error and the retry schedule
            }
        }

        CacheChanged?.Invoke();
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

    /// <summary>
    /// The manager's one ticking clock: re-reads Influx status, rebuilds failed bridges on
    /// their backoff schedule and keeps resyncing a connected bridge whose post-reconnect
    /// snapshot refresh has not landed yet. Re-entrancy guarded — a slow tick is skipped,
    /// not stacked.
    /// </summary>
    private void StartHousekeeping()
    {
        lock (sync_)
        {
            housekeepingTimer_?.Dispose();
            housekeepingTimer_ = new System.Threading.Timer(
                _ => HousekeepingAsync(),
                null,
                options_.PollInterval,
                options_.PollInterval);
        }
    }

    private async void HousekeepingAsync()
    {
        if (Interlocked.Exchange(ref ticking_, 1) == 1)
        {
            return;
        }

        try
        {
            BridgeSession[] sessions;
            lock (sync_)
            {
                sessions = sessions_.Values.ToArray();
            }

            foreach (BridgeSession session in sessions)
            {
                if (session.Retired)
                {
                    continue;
                }

                if (session.LinkState == BridgeLinkState.Failed && DateTime.UtcNow >= session.NextRetryUtc)
                {
                    try
                    {
                        await ConnectSessionCoreAsync(session, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // stays Failed with its backoff; the next due tick tries again
                    }
                }

                if (session.LinkState == BridgeLinkState.Connected && session.NeedsResync)
                {
                    await ResyncAsync(session).ConfigureAwait(false);
                }
            }

            foreach (BridgeSession session in sessions)
            {
                if (session.Retired)
                {
                    continue;
                }

                try
                {
                    await RefreshInfluxStatusAsync(session.BridgeId, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // transient poll failure: leave the last known state as-is
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref ticking_, 0);
        }
    }

    /// <summary>
    /// Re-fetches a bridge's full tag snapshot after its link came back, retrying so the one
    /// swallowed failure can no longer leave pre-outage values on screen. If even the retry
    /// schedule fails, <see cref="BridgeSession.NeedsResync"/> stays set and the housekeeping
    /// tick keeps trying until the values are actually fresh.
    /// </summary>
    private async Task ResyncAsync(BridgeSession session)
    {
        bool refreshed = await SnapshotRefresh.TryAsync(
            async token =>
            {
                HmiTagsResponse snapshot = await session.Api.GetTagsAsync(token).ConfigureAwait(false);
                Cache.ReplaceBridge(session.BridgeId, snapshot.Tags);
                session.MappingVersion = snapshot.Version;
                CacheChanged?.Invoke();
            },
            options_.SnapshotRetryDelays,
            CancellationToken.None).ConfigureAwait(false);

        lock (sync_)
        {
            if (session.Retired)
            {
                return;
            }

            session.NeedsResync = !refreshed;
        }

        if (!refreshed)
        {
            return;
        }

        try
        {
            await RefreshInfluxStatusAsync(session.BridgeId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // transient; the housekeeping poll re-reads it
        }

        Func<string, Task>? reconnected = BridgeReconnected;
        if (reconnected is not null)
        {
            await reconnected(session.BridgeId).ConfigureAwait(false);
        }
    }

    private Task HandleReconnectingAsync(BridgeSession session, Exception? error)
    {
        lock (sync_)
        {
            session.NeedsResync = true;
        }

        SetLinkState(session, BridgeLinkState.Reconnecting, error?.Message);
        return Task.CompletedTask;
    }

    private async Task HandleReconnectedAsync(BridgeSession session)
    {
        SetLinkState(session, BridgeLinkState.Connected, null);
        await ResyncAsync(session).ConfigureAwait(false);
    }

    private Task HandleClosedAsync(BridgeSession session, Exception? error)
    {
        SetLinkState(session, BridgeLinkState.Failed, error?.Message ?? "connection closed");
        ScheduleRetry(session);
        return Task.CompletedTask;
    }

    private void SetLinkState(BridgeSession session, BridgeLinkState state, string? error)
    {
        bool changed;
        lock (sync_)
        {
            if (session.Retired)
            {
                return;
            }

            changed = session.LinkState != state
                || !string.Equals(session.LastError, error, StringComparison.Ordinal);
            session.LinkState = state;
            session.LastError = error;
        }

        if (changed)
        {
            LinkStateChanged?.Invoke(session.BridgeId, state, error);
        }
    }

    private void ScheduleRetry(BridgeSession session)
    {
        lock (sync_)
        {
            if (session.Retired)
            {
                return;
            }

            int index = Math.Min(session.RetryCount, options_.BridgeRetryDelays.Count - 1);
            session.RetryCount++;
            session.NextRetryUtc = DateTime.UtcNow + options_.BridgeRetryDelays[index];
        }
    }

    public async Task DisconnectAllAsync()
    {
        lock (sync_)
        {
            housekeepingTimer_?.Dispose();
            housekeepingTimer_ = null;
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
        public BridgeSession(string bridgeId, HmiBridgeEndpoint endpoint)
        {
            BridgeId = bridgeId;
            Endpoint = endpoint;
            Api = new BridgeApiClient();
            Api.SetBaseAddress(endpoint.BaseUrl);
        }

        public string BridgeId { get; }
        public HmiBridgeEndpoint Endpoint { get; }
        public string BaseUrl => Endpoint.BaseUrl;
        public BridgeApiClient Api { get; }
        public HmiHubClient? Hub { get; internal set; }
        public long MappingVersion { get; set; }

        /// <summary>Last known live InfluxDB writer connection state for this bridge.</summary>
        public bool InfluxConnected { get; set; }

        /// <summary>Live link state; starts Failed until a connect attempt succeeds.</summary>
        public BridgeLinkState LinkState { get; internal set; } = BridgeLinkState.Failed;

        /// <summary>Message behind the last failure, for the status surfaces.</summary>
        public string? LastError { get; internal set; }

        /// <summary>Set when the link drops; cleared once a post-reconnect snapshot refresh lands.</summary>
        public bool NeedsResync { get; internal set; }

        /// <summary>Consecutive failed connect attempts, for the retry backoff.</summary>
        public int RetryCount { get; internal set; }

        /// <summary>When the housekeeping tick may try this failed bridge again.</summary>
        public DateTime NextRetryUtc { get; internal set; } = DateTime.MinValue;

        /// <summary>Set before teardown, so late hub events cannot publish a state for a dead session.</summary>
        public bool Retired { get; internal set; }

        public async ValueTask DisposeAsync()
        {
            Retired = true;
            if (Hub is not null)
            {
                await Hub.DisposeAsync().ConfigureAwait(false);
                Hub = null;
            }

            Api.Dispose();
        }
    }
}
