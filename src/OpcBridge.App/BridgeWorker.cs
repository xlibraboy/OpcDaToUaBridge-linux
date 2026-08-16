using System.Diagnostics;
using System.Globalization;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Mqtt;
using OpcBridge.Influx;
using OpcBridge.Ua;
using System.Threading.Channels;
using System.Text.Json;


namespace OpcBridge.App;

public sealed class BridgeWorker : BackgroundService, IDaLinkMetadataResolver
{
    private const int CoordinatorTickMs = 200;

    private readonly UaServerHost ua_server_;
    private readonly BridgeState bridge_state_;
    private readonly MappingStore mapping_store_;
    private readonly DaLinkStore da_link_store_;
    private readonly DaRuntimeSettings da_settings_;
    private readonly SourceClientFactory da_client_factory_;
    private readonly ILogger<BridgeWorker> logger_;
    private readonly IReadOnlyDictionary<int, int> rate_limits_;
    private readonly ConcurrentDictionary<string, DateTime> watchdog_activity_ = new(StringComparer.OrdinalIgnoreCase);
    private int backoffMs_ = 1000;
    private WriteQueue? write_queue_;
    private volatile Dictionary<string, SourceSession>? active_sessions_;
    private readonly IMqttBridge mqtt_bridge_;
    private readonly MqttRuntimeSettings mqtt_settings_;
    private readonly MqttValueStore mqtt_values_;
    private readonly Channel<BridgeValue> mqtt_publish_channel_ = Channel.CreateBounded<BridgeValue>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private HashSet<string> mqtt_enabled_keys_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly IInfluxWriter influx_writer_;
    private readonly InfluxRuntimeSettings influx_settings_;
    private readonly Channel<BridgeValue> influx_write_channel_ = Channel.CreateBounded<BridgeValue>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private HashSet<string> influx_enabled_keys_ = new(StringComparer.OrdinalIgnoreCase);
    private volatile SourceMappingCache? source_mapping_cache_;

    public BridgeWorker(
        UaServerHost uaServer,
        BridgeState bridgeState,
        MappingStore mappingStore,
        DaLinkStore daLinkStore,
        DaRuntimeSettings daSettings,
        SourceClientFactory daClientFactory,
        IOptions<BridgeOptions> bridgeOptions,
        ILogger<BridgeWorker> logger,
        IMqttBridge mqttBridge,
        MqttRuntimeSettings mqttSettings,
        MqttValueStore mqttValues,
        IInfluxWriter influxWriter,
        InfluxRuntimeSettings influxSettings)
    {
        ua_server_ = uaServer;
        bridge_state_ = bridgeState;
        mapping_store_ = mappingStore;
        da_link_store_ = daLinkStore;
        da_settings_ = daSettings;
        da_client_factory_ = daClientFactory;
        logger_ = logger;
        rate_limits_ = bridgeOptions.Value.RateLimits;
        mqtt_bridge_ = mqttBridge;
        mqtt_settings_ = mqttSettings;
        mqtt_values_ = mqttValues;
        influx_writer_ = influxWriter;
        influx_settings_ = influxSettings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DaRuntimeSettingsSnapshot settings = da_settings_.GetSnapshot();
        (IReadOnlyList<TagMapping> mappings, long mappingVersion) = mapping_store_.GetSnapshot();
        (IReadOnlyList<DaLinkRule> rules, long daLinkVersion) = da_link_store_.GetSnapshot();
        SourceMappingCache sourceMappingCache = SourceMappingCache.Build(mappings, rules);
        source_mapping_cache_ = sourceMappingCache;
        IReadOnlyList<TagMapping> activeMappings = sourceMappingCache.GetActiveMappings();
        bridge_state_.Configure(settings.UpdateRateMs, activeMappings.Count, settings.Sources);

        mqtt_enabled_keys_ = new HashSet<string>(
            activeMappings.Where(m => m.MqttEnabled).Select(m => NormalizeKey(m.SourceId, m.ItemId)),
            StringComparer.OrdinalIgnoreCase);
        influx_enabled_keys_ = new HashSet<string>(
            activeMappings.Where(m => m.InfluxEnabled).Select(m => NormalizeKey(m.SourceId, m.ItemId)),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            await ua_server_.StartAsync(activeMappings, stoppingToken).ConfigureAwait(false);

            write_queue_ = new WriteQueue();
            ua_server_.SetWriteHandler((value, tcs) =>
            {
                if (write_queue_ is null)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                // Non-blocking enqueue; the per-source consumer resolves the TCS.
                write_queue_.Enqueue(value.SourceId, new WriteRequest(value.SourceId, value.ItemId, value.Value, tcs));
            });

            long uaMappingVersion = mappingVersion;
            long appliedDaLinkVersion = daLinkVersion;
            long connectedVersion = -1;
            Dictionary<string, SourceSession> sessions = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Task> pollers = new(StringComparer.OrdinalIgnoreCase);
            SharedCacheHolder cacheHolder = new(sourceMappingCache);
            ConcurrentQueue<string> failedSourceQueue = new();

            mqtt_bridge_.SetMessageSink(OnMqttInboundAsync);
            mqtt_bridge_.StateChanged += state =>
            {
                mqtt_settings_.SetState(state.ToString(), state == MqttConnectionState.Faulted ? "MQTT broker connection failed." : null);
                if (state == MqttConnectionState.Connected)
                {
                    mqtt_settings_.ResetCounters();
                }
            };
            influx_writer_.StateChanged += state =>
            {
                influx_settings_.SetState(state.ToString(), state == InfluxConnectionState.Faulted ? "Influx connection failed." : null);
                if (state == InfluxConnectionState.Connected)
                {
                    influx_settings_.ResetCounters();
                }
            };
            bridge_state_.ValueUpdated += OnBridgeValueUpdated;
            _ = Task.Run(() => MqttPublishDrainAsync(stoppingToken), stoppingToken);
            _ = Task.Run(() => InfluxWriteDrainAsync(stoppingToken), stoppingToken);

            if (mqtt_settings_.GetOptions().Enabled)
            {
                _ = ConnectMqttAsync(stoppingToken);
            }

            if (influx_settings_.GetOptions().Enabled)
            {
                _ = ConnectInfluxAsync(stoppingToken);
            }

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    settings = da_settings_.GetSnapshot();
                    (mappings, mappingVersion) = mapping_store_.GetSnapshot();
                    (rules, daLinkVersion) = da_link_store_.GetSnapshot();

                    try
                    {
                        ScanWatchdog(sessions, failedSourceQueue);

                        if (!failedSourceQueue.IsEmpty)
                        {
                            HashSet<string> failedIds = new(StringComparer.OrdinalIgnoreCase);
                            while (failedSourceQueue.TryDequeue(out string? failedId))
                            {
                                if (!string.IsNullOrWhiteSpace(failedId))
                                {
                                    failedIds.Add(failedId);
                                }
                            }

                            foreach (string failedId in failedIds)
                            {
                                sessions.TryGetValue(failedId, out SourceSession? failedSession);
                                await StopPollersForSourceAsync(pollers, failedId, failedSession).ConfigureAwait(false);
                                if (sessions.Remove(failedId, out SourceSession? removed))
                                {
                                    await removed.Client.DisposeAsync().ConfigureAwait(false);
                                    bridge_state_.ClearSourceValues(failedId);
                                    bridge_state_.SetSourceConnectionState(failedId, "Reconnecting");
                                    bridge_state_.SetSourceError(failedId, new InvalidOperationException(
                                        "Connection lost — reconnecting automatically."));
                                    logger_.LogWarning("Source {SourceId} connection lost; reconnecting with backoff", failedId);
                                }

                                watchdog_activity_.TryRemove(failedId, out _);
                            }

                            if (failedIds.Count > 0)
                            {
                                connectedVersion = -1;
                            }
                        }

                        bool mappingsChanged = mappingVersion != uaMappingVersion;
                        bool rulesChanged = daLinkVersion != appliedDaLinkVersion;
                        if (mappingsChanged || rulesChanged)
                        {
                            // Snapshot each source's current distinct rate set before the cache is
                            // rebuilt, so a mapping change only restarts pollers whose rate set
                            // actually changed.
                            Dictionary<string, HashSet<int>> previousRates = new(StringComparer.OrdinalIgnoreCase);
                            if (mappingsChanged)
                            {
                                foreach (SourceSession session in sessions.Values)
                                {
                                    previousRates[session.Source.SourceId] = cacheHolder.Cache
                                        .GetDistinctRates(session.Source.SourceId, settings.UpdateRateMs)
                                        .ToHashSet();
                                }
                            }

                            cacheHolder.Cache = SourceMappingCache.Build(mappings, rules);
                            source_mapping_cache_ = cacheHolder.Cache;

                            if (mappingsChanged)
                            {
                                activeMappings = cacheHolder.Cache.GetActiveMappings();
                                mqtt_enabled_keys_ = new HashSet<string>(
                                    activeMappings.Where(m => m.MqttEnabled).Select(m => NormalizeKey(m.SourceId, m.ItemId)),
                                    StringComparer.OrdinalIgnoreCase);
                                influx_enabled_keys_ = new HashSet<string>(
                                    activeMappings.Where(m => m.InfluxEnabled).Select(m => NormalizeKey(m.SourceId, m.ItemId)),
                                    StringComparer.OrdinalIgnoreCase);
                                ua_server_.SyncMappings(activeMappings);
                                bridge_state_.RetainMappedValues(activeMappings);
                                uaMappingVersion = mappingVersion;
                                await ReconcileUaMonitoredItemsAsync(
                                    sessions,
                                    cacheHolder.Cache,
                                    stoppingToken).ConfigureAwait(false);

                                // DA clients bind items into rate groups at connect — rebuild only those.
                                HashSet<string> daDirty = new(StringComparer.OrdinalIgnoreCase);
                                foreach (SourceSession session in sessions.Values)
                                {
                                    if (session.Client is OpcDaClient)
                                    {
                                        daDirty.Add(session.Source.SourceId);
                                    }
                                }

                                if (daDirty.Count > 0)
                                {
                                    foreach (string id in daDirty)
                                    {
                                        sessions.TryGetValue(id, out SourceSession? sess);
                                        await StopPollersForSourceAsync(pollers, id, sess).ConfigureAwait(false);
                                    }

                                    (_, bool forceRebuildConnectionFailures) = await ReconfigureSessionsAsync(
                                        settings,
                                        sessions,
                                        stoppingToken,
                                        forceRebuildSourceIds: daDirty).ConfigureAwait(false);

                                    // A dirty source that failed to reconnect gets retried
                                    // by the main reconfigure branch with backoff.
                                    if (forceRebuildConnectionFailures)
                                    {
                                        connectedVersion = -1;
                                    }
                                    active_sessions_ = new Dictionary<string, SourceSession>(sessions, StringComparer.OrdinalIgnoreCase);
                                    await RestartPollersForSourcesAsync(
                                        settings,
                                        sessions,
                                        cacheHolder,
                                        failedSourceQueue,
                                        pollers,
                                        daDirty.Where(id => sessions.ContainsKey(id)),
                                        stoppingToken).ConfigureAwait(false);
                                }

                                // Non-DA clients (MX Component, serial drivers, UA sources) do not
                                // bind items into OPC groups at connect — their pollers are created
                                // per distinct mapping rate and re-read the cache every cycle. A
                                // per-tag poll-rate change can introduce a rate group with no running
                                // poller (values freeze) or leave a stale poller behind, so restart
                                // their pollers whenever the rate set changed.
                                HashSet<string> nonDaDirty = new(StringComparer.OrdinalIgnoreCase);
                                foreach (SourceSession session in sessions.Values)
                                {
                                    if (session.Client is OpcDaClient)
                                    {
                                        continue;
                                    }

                                    HashSet<int> newRates = cacheHolder.Cache
                                        .GetDistinctRates(session.Source.SourceId, settings.UpdateRateMs)
                                        .ToHashSet();
                                    if (!previousRates.TryGetValue(session.Source.SourceId, out HashSet<int>? oldRates)
                                        || !oldRates.SetEquals(newRates))
                                    {
                                        nonDaDirty.Add(session.Source.SourceId);
                                    }
                                }

                                if (nonDaDirty.Count > 0)
                                {
                                    await RestartPollersForSourcesAsync(
                                        settings,
                                        sessions,
                                        cacheHolder,
                                        failedSourceQueue,
                                        pollers,
                                        nonDaDirty,
                                        stoppingToken).ConfigureAwait(false);
                                }
                            }

                            if (rulesChanged)
                            {
                                appliedDaLinkVersion = daLinkVersion;
                            }
                        }

                        if (connectedVersion != settings.Version)
                        {
                            bridge_state_.Configure(settings.UpdateRateMs, activeMappings.Count, settings.Sources);
                            // Snapshot current ids so we can stop pollers for removed sources too.
                            HashSet<string> beforeIds = sessions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                            // Stop all current pollers that might be rebuilt — cheap: stop only candidates
                            // after we know changed set. Stop-all-then-start-changed is wrong for dirty-only.
                            // Instead: stop pollers for every existing source id first only if Version change
                            // could remove them — compute desired first.
                            HashSet<string> desiredIds = settings.Sources
                                .Select(s => s.SourceId)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            HashSet<string> preStop = new(StringComparer.OrdinalIgnoreCase);
                            foreach (string id in beforeIds)
                            {
                                if (!desiredIds.Contains(id))
                                {
                                    preStop.Add(id);
                                }
                            }
                            // Also pre-stop sources whose connection settings changed.
                            foreach (DaSourceRuntimeSettings src in settings.Sources)
                            {
                                if (sessions.TryGetValue(src.SourceId, out SourceSession? existing)
                                    && !SourceConnectionEquals(existing.Source, src))
                                {
                                    preStop.Add(src.SourceId);
                                }
                                else if (!sessions.ContainsKey(src.SourceId))
                                {
                                    preStop.Add(src.SourceId);
                                }
                            }

                            foreach (string id in preStop)
                            {
                                sessions.TryGetValue(id, out SourceSession? sess);
                                await StopPollersForSourceAsync(pollers, id, sess).ConfigureAwait(false);
                            }

                            (HashSet<string> changed, bool connectionFailures) = await ReconfigureSessionsAsync(
                                settings,
                                sessions,
                                stoppingToken).ConfigureAwait(false);
                            active_sessions_ = new Dictionary<string, SourceSession>(sessions, StringComparer.OrdinalIgnoreCase);
                            if (changed.Count > 0)
                            {
                                await RestartPollersForSourcesAsync(
                                    settings,
                                    sessions,
                                    cacheHolder,
                                    failedSourceQueue,
                                    pollers,
                                    changed.Where(id => sessions.ContainsKey(id)),
                                    stoppingToken).ConfigureAwait(false);
                            }

                            if (connectionFailures)
                            {
                                // Some sources could not connect (server unreachable) — keep
                                // connectedVersion stale so the next tick retries, with
                                // exponential backoff. Pollers for successfully connected
                                // sources above already started.
                                await Task.Delay(backoffMs_, stoppingToken).ConfigureAwait(false);
                                backoffMs_ = Math.Min(backoffMs_ * 2, 5000);
                            }
                            else
                            {
                                connectedVersion = settings.Version;
                                backoffMs_ = 1000;
                            }
                        }

                        await Task.Delay(CoordinatorTickMs, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        bridge_state_.SetError(exception);
                        logger_.LogError(exception, "Bridge coordinator loop failed");
                        await StopPollersAsync(pollers, sessions).ConfigureAwait(false);
                        await DisposeSessionsAsync(sessions).ConfigureAwait(false);
                        sessions.Clear();
                        // Never leave the version stale-valid: re-evaluate every source
                        // (including reconnects) on the next tick.
                        connectedVersion = -1;
                        await Task.Delay(backoffMs_, stoppingToken).ConfigureAwait(false);
                        backoffMs_ = Math.Min(backoffMs_ * 2, 5000);
                    }
                }
            }
            finally
            {
                await StopPollersAsync(pollers, sessions).ConfigureAwait(false);
                await DisposeSessionsAsync(sessions).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            bridge_state_.SetBridgeState("Stopping");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        bridge_state_.SetBridgeState("Stopping");
        try
        {
            await influx_writer_.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger_.LogWarning(ex, "Influx disconnect failed during stop");
        }
        await ua_server_.StopAsync(cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        bridge_state_.SetDaConnectionState("Disconnected");
        bridge_state_.SetBridgeState("Stopped");
    }

    private void StartPollers(
        DaRuntimeSettingsSnapshot settings,
        Dictionary<string, SourceSession> sessions,
        SharedCacheHolder cacheHolder,
        ConcurrentQueue<string> failedSourceQueue,
        Dictionary<string, Task> pollers,
        CancellationToken stoppingToken,
        IEnumerable<string>? onlySourceIds = null)
    {
        HashSet<string>? filter = onlySourceIds is null
            ? null
            : onlySourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        SourceMappingCache cache = cacheHolder.Cache;

        for (int i = 0; i < settings.Sources.Count; i++)
        {
            DaSourceRuntimeSettings source = settings.Sources[i];
            if (filter is not null && !filter.Contains(source.SourceId))
            {
                continue;
            }

            if (!sessions.TryGetValue(source.SourceId, out SourceSession? session))
            {
                continue;
            }

            CancellationTokenSource sourceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            session.PollerCts?.Dispose();
            session.PollerCts = sourceCts;
            CancellationToken pollerToken = sourceCts.Token;

            IReadOnlyList<int> rates = cache.GetDistinctRates(source.SourceId, settings.UpdateRateMs);
            foreach (int rate in rates)
            {
                string pollerKey = $"{source.SourceId}:{rate}";
                pollers[pollerKey] = Task.Run(() => RunSourcePollerAsync(
                    source,
                    session,
                    rate,
                    cacheHolder,
                    failedSourceQueue,
                    pollerToken));
            }

            if (write_queue_ is not null)
            {
                string writerKey = $"{source.SourceId}:write";
                pollers[writerKey] = Task.Run(() => ProcessWriteQueueAsync(source.SourceId, session, write_queue_, pollerToken));
            }
        }
    }

    private async Task RestartPollersForSourcesAsync(
        DaRuntimeSettingsSnapshot settings,
        Dictionary<string, SourceSession> sessions,
        SharedCacheHolder cacheHolder,
        ConcurrentQueue<string> failedSourceQueue,
        Dictionary<string, Task> pollers,
        IEnumerable<string> sourceIds,
        CancellationToken stoppingToken)
    {
        foreach (string sourceId in sourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase))
        {
            sessions.TryGetValue(sourceId, out SourceSession? session);
            await StopPollersForSourceAsync(pollers, sourceId, session).ConfigureAwait(false);
        }

        StartPollers(settings, sessions, cacheHolder, failedSourceQueue, pollers, stoppingToken, sourceIds);
    }

    private async Task RunSourcePollerAsync(
        DaSourceRuntimeSettings source,
        SourceSession session,
        int rate,
        SharedCacheHolder cacheHolder,
        ConcurrentQueue<string> failedSourceQueue,
        CancellationToken pollerToken)
    {
        while (!pollerToken.IsCancellationRequested)
        {
            int delayRate = rate;

            try
            {
                DaRuntimeSettingsSnapshot currentSettings = da_settings_.GetSnapshot();
                int defaultRate = currentSettings.UpdateRateMs;

                SourceMappingCache cache = cacheHolder.Cache;
                IReadOnlyList<TagMapping> sourceReadMappings = cache.GetSourceReadMappingsByRate(source.SourceId, rate, defaultRate);
                IReadOnlyList<TagMapping> manualMappings = cache.GetManualMappings(source.SourceId);

                Stopwatch cycleTimer = Stopwatch.StartNew();
                SourcePollResult result = await PollSourceAsync(
                    source,
                    session,
                    sourceReadMappings,
                    manualMappings,
                    cache,
                    pollerToken).ConfigureAwait(false);
                cycleTimer.Stop();

                bridge_state_.MarkUaWrite(result.OutputValueCount, cycleTimer.Elapsed);
                bridge_state_.UpdateRateGroup(source.SourceId, rate, sourceReadMappings.Count, GetRateLimit(rate), cycleTimer.Elapsed);

                if (!result.ReadSucceeded)
                {
                    failedSourceQueue.Enqueue(source.SourceId);
                }
            }
            catch (OperationCanceledException) when (pollerToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger_.LogError(exception, "Source {SourceId} rate {Rate}ms poller failed", source.SourceId, rate);
                failedSourceQueue.Enqueue(source.SourceId);
            }

            try
            {
                await Task.Delay(delayRate, pollerToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pollerToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
    private async Task ProcessWriteQueueAsync(
        string sourceId,
        SourceSession session,
        WriteQueue writeQueue,
        CancellationToken cancellationToken)
    {
        // Each source's consumer reads only that source's channel (WriteQueue routes by
        // source at enqueue time), so no cross-source re-enqueue is ever needed.
        await foreach (WriteRequest req in writeQueue.ReaderAsync(sourceId, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                bool success = await session.Client.WriteAsync(req.ItemId, req.Value, cancellationToken).ConfigureAwait(false);
                req.Tcs.TrySetResult(success);
                writeQueue.RecordResult(success);
            }
            catch (Exception ex)
            {
                req.Tcs.TrySetException(ex);
                writeQueue.RecordResult(false);
            }
        }
    }


    private static async Task StopPollersForSourceAsync(
        Dictionary<string, Task> pollers,
        string sourceId,
        SourceSession? session)
    {
        try { session?.PollerCts?.Cancel(); } catch (ObjectDisposedException) { }

        string prefix = sourceId + ":";
        List<Task> tasks = new();
        foreach (string key in pollers.Keys.ToArray())
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pollers.Remove(key, out Task? task))
            {
                tasks.Add(task);
            }
        }

        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (session is not null)
        {
            session.PollerCts?.Dispose();
            session.PollerCts = null;
        }
    }

    private static async Task StopPollersAsync(
        Dictionary<string, Task> pollers,
        Dictionary<string, SourceSession> sessions)
    {
        foreach (SourceSession session in sessions.Values)
        {
            try { session.PollerCts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        Task[] tasks = pollers.Values.ToArray();
        pollers.Clear();

        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        foreach (SourceSession session in sessions.Values)
        {
            session.PollerCts?.Dispose();
            session.PollerCts = null;
        }
    }

    private int GetRateLimit(int rateMs)
    {
        return rate_limits_.TryGetValue(rateMs, out int limit) ? limit : 0;
    }

    /// <summary>
    /// Subscription-mode health check: when a subscription is active the poller performs
    /// no device reads, so a dead server is only detectable through callback traffic.
    /// A source whose values have stopped arriving for longer than
    /// <see cref="DaSourceRuntimeSettings.WatchdogTimeoutMs"/> is enqueued for teardown
    /// and reconnect. Sources that never delivered a value (e.g. static tags) are left
    /// alone so quiet-but-healthy subscriptions do not flap.
    /// </summary>
    private void ScanWatchdog(Dictionary<string, SourceSession> sessions, ConcurrentQueue<string> failedSourceQueue)
    {
        DateTime now = DateTime.UtcNow;
        foreach (SourceSession session in sessions.Values)
        {
            if (session.Client is not ISubscriptionActiveSource subscribed || !subscribed.IsSubscriptionActive)
            {
                continue;
            }

            int timeoutMs = session.Source.WatchdogTimeoutMs;
            if (timeoutMs <= 0)
            {
                continue;
            }

            if (!watchdog_activity_.TryGetValue(session.Source.SourceId, out DateTime lastActivity))
            {
                continue;
            }

            double elapsedMs = (now - lastActivity).TotalMilliseconds;
            if (elapsedMs <= timeoutMs)
            {
                continue;
            }

            logger_.LogWarning(
                "Source {SourceId} subscription watchdog: no values for {ElapsedMs}ms (limit {TimeoutMs}ms); reconnecting",
                session.Source.SourceId,
                (long)elapsedMs,
                timeoutMs);
            failedSourceQueue.Enqueue(session.Source.SourceId);
        }
    }
    private async Task<SourcePollResult> PollSourceAsync(
        DaSourceRuntimeSettings source,
        SourceSession session,
        IReadOnlyList<TagMapping> sourceReadMappings,
        IReadOnlyList<TagMapping> manualMappings,
        SourceMappingCache cache,
        CancellationToken cancellationToken)
    {
        int outputValueCount = 0;
        bool sourceReadSucceeded = false;

        try
        {
            bridge_state_.SetSourceConnectionState(source.SourceId, "Connected");
            Stopwatch readTimer = Stopwatch.StartNew();
            IReadOnlyList<BridgeValue> values = await Task
                .Run(async () => await session.Client.ReadAsync(sourceReadMappings, cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);

            readTimer.Stop();
            bridge_state_.UpdateDaRead(source.SourceId, values, readTimer.Elapsed);
            for (int valueIndex = 0; valueIndex < values.Count; valueIndex++)
            {
                BridgeValue value = values[valueIndex];
                bridge_state_.SetValue(value);
                ua_server_.UpdateValue(value);
                outputValueCount++;

                ForwardToConsumers(value, cache, cancellationToken);
            }

            sourceReadSucceeded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            bridge_state_.SetSourceConnectionState(source.SourceId, "Faulted");
            bridge_state_.SetSourceError(source.SourceId, exception);
            ClearReadValues(sourceReadMappings);
            logger_.LogWarning(exception, "Source {SourceId} read failed", source.SourceId);
        }

        outputValueCount += ApplyManualMappings(manualMappings);

        return sourceReadSucceeded
            ? SourcePollResult.Success(source.SourceId, outputValueCount)
            : SourcePollResult.Failure(source.SourceId, outputValueCount);
    }

    private int ApplyManualMappings(IReadOnlyList<TagMapping> manualMappings)
    {
        int updatedCount = 0;

        for (int i = 0; i < manualMappings.Count; i++)
        {
            TagMapping mapping = manualMappings[i];
            if (!TryCreateManualValue(mapping, out BridgeValue manualValue))
            {
                bridge_state_.ClearValue(mapping.SourceId, mapping.ItemId);
                continue;
            }

            bridge_state_.SetValue(manualValue);
            ua_server_.UpdateValue(manualValue);
            updatedCount++;
        }
        return updatedCount;
    }

    /// <summary>
    /// Forwards a provider tag's value into every enabled consumer that links to it.
    /// Gated by the provider's AccessRights (must allow Read) and the consumer's AccessRights
    /// (must allow Write / Read-Write). Cross-source links are supported: the WriteQueue routes
    /// each request to the consumer's own source session.
    /// </summary>
    private void ForwardToConsumers(BridgeValue providerValue, SourceMappingCache cache, CancellationToken cancellationToken)
    {
        if (write_queue_ is null)
        {
            return;
        }

        IReadOnlyList<TagMapping> consumers = cache.GetConsumersByProvider(providerValue.SourceId, providerValue.ItemId);
        if (consumers.Count == 0)
        {
            return;
        }

        // The provider itself must permit reads for forwarding to make sense.
        bool providerReadable = false;
        foreach (TagMapping providerMapping in cache.GetMappings(providerValue.SourceId))
        {
            if (string.Equals(providerMapping.ItemId, providerValue.ItemId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(providerMapping.AccessRights, TagAccessRights.Write, StringComparison.OrdinalIgnoreCase))
            {
                providerReadable = true;
                break;
            }
        }

        if (!providerReadable)
        {
            return;
        }

        if (!providerValue.IsGood)
        {
            // Don't forward bad-quality values into the target.
            return;
        }

        for (int i = 0; i < consumers.Count; i++)
        {
            TagMapping consumer = consumers[i];
            if (!consumer.Enabled)
            {
                continue;
            }

            if (!string.Equals(consumer.AccessRights, TagAccessRights.Write, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(consumer.AccessRights, TagAccessRights.ReadWrite, StringComparison.OrdinalIgnoreCase))
            {
                // Consumer cannot accept writes; skip.
                continue;
            }

            TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            write_queue_.Enqueue(
                consumer.SourceId,
                new WriteRequest(consumer.SourceId, consumer.ItemId, providerValue.Value, tcs));
        }
    }

    private void ClearReadValues(IReadOnlyList<TagMapping> readMappings)
    {
        for (int i = 0; i < readMappings.Count; i++)
        {
            TagMapping mapping = readMappings[i];
            bridge_state_.ClearValue(mapping.SourceId, mapping.ItemId);
        }
    }

    private async Task<(HashSet<string> Changed, bool ConnectionFailures)> ReconfigureSessionsAsync(
        DaRuntimeSettingsSnapshot settings,
        Dictionary<string, SourceSession> sessions,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? forceRebuildSourceIds = null)
    {
        HashSet<string> changed = new(StringComparer.OrdinalIgnoreCase);
        bool connectionFailures = false;

        HashSet<string> desiredSources = settings.Sources
            .Select(source => source.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach ((string sourceId, SourceSession session) in sessions.ToArray())
        {
            if (desiredSources.Contains(sourceId))
            {
                continue;
            }

            await session.Client.DisposeAsync().ConfigureAwait(false);
            sessions.Remove(sourceId);
            bridge_state_.ClearSourceValues(sourceId);
            watchdog_activity_.TryRemove(sourceId, out _);
            changed.Add(sourceId);
        }

        for (int i = 0; i < settings.Sources.Count; i++)
        {
            DaSourceRuntimeSettings source = settings.Sources[i];
            bool force = forceRebuildSourceIds is not null
                && forceRebuildSourceIds.Contains(source.SourceId);

            if (sessions.TryGetValue(source.SourceId, out SourceSession? existing)
                && !force
                && SourceConnectionEquals(existing.Source, source))
            {
                // Connection knobs unchanged — keep live client; refresh settings snapshot only if display-only.
                if (!SourceSettingsEquals(existing.Source, source))
                {
                    sessions[source.SourceId] = new SourceSession(source, existing.Client)
                    {
                        PollerCts = existing.PollerCts
                    };
                    // Rate/subscription changes still need poller restart.
                    if (existing.Source.UpdateRateMs != source.UpdateRateMs
                        || existing.Source.UseSubscriptions != source.UseSubscriptions)
                    {
                        changed.Add(source.SourceId);
                    }
                }
                continue;
            }

            if (sessions.Remove(source.SourceId, out SourceSession? oldSession))
            {
                try { oldSession.PollerCts?.Cancel(); } catch (ObjectDisposedException) { }
                oldSession.PollerCts?.Dispose();
                await oldSession.Client.DisposeAsync().ConfigureAwait(false);
                changed.Add(source.SourceId);
            }

            if (string.Equals(source.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source.SourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(source.SerialPortName))
                {
                    bridge_state_.SetSourceConnectionState(source.SourceId, "Disconnected");
                    bridge_state_.SetSourceError(source.SourceId, new InvalidOperationException("Serial port is empty — enter a COM port (e.g. /dev/ttyUSB0)."));
                    logger_.LogWarning("Source {SourceId} has no serial port, skipping connection", source.SourceId);
                    changed.Add(source.SourceId);
                    continue;
                }
            }
            else if (string.Equals(source.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(source.EndpointUrl))
                {
                    bridge_state_.SetSourceConnectionState(source.SourceId, "Disconnected");
                    bridge_state_.SetSourceError(source.SourceId, new InvalidOperationException(
                        "EndpointUrl is empty — enter a valid OPC UA server endpoint (opc.tcp://...)."));
                    logger_.LogWarning("Source {SourceId} has no EndpointUrl, skipping connection", source.SourceId);
                    changed.Add(source.SourceId);
                    continue;
                }

                string serverEndpointUrl = ua_server_.GetOptions().EndpointUrl;
                if (UaEndpointGuard.TargetsSelf(source.EndpointUrl, serverEndpointUrl))
                {
                    bridge_state_.SetSourceConnectionState(source.SourceId, "Faulted");
                    bridge_state_.SetSourceError(source.SourceId, new InvalidOperationException(
                        "Cannot use this process's own OPC UA server endpoint as a source."));
                    logger_.LogWarning(
                        "Source {SourceId} EndpointUrl {EndpointUrl} targets own UA server {ServerEndpoint}, refusing connect",
                        source.SourceId,
                        source.EndpointUrl,
                        serverEndpointUrl);
                    changed.Add(source.SourceId);
                    continue;
                }
            }
            else if (string.Equals(source.SourceType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
            {
                if (source.LogicalStationNumber is < 0 or > 1023)
                {
                    bridge_state_.SetSourceConnectionState(source.SourceId, "Disconnected");
                    bridge_state_.SetSourceError(source.SourceId, new InvalidOperationException(
                        "Logical station number must be between 0 and 1023 — configure the station in MX Component's Communication Settings Utility."));
                    logger_.LogWarning("Source {SourceId} has an invalid logical station, skipping connection", source.SourceId);
                    changed.Add(source.SourceId);
                    continue;
                }
            }
            else if (string.IsNullOrWhiteSpace(source.ProgId))
            {
                bridge_state_.SetSourceConnectionState(source.SourceId, "Disconnected");
                bridge_state_.SetSourceError(source.SourceId, new InvalidOperationException("ProgID is empty — enter a valid OPC DA server ProgID."));
                logger_.LogWarning("Source {SourceId} has no ProgID, skipping connection", source.SourceId);
                changed.Add(source.SourceId);
                continue;
            }

            try
            {
                bridge_state_.SetSourceConnectionState(source.SourceId, "Connecting");
                ISourceClient client = da_client_factory_.Create(settings, source);
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

                if (client is ISubscribableSourceClient subscribable)
                {
                    subscribable.ValuesReceived += values => OnSubscriptionValues(values);
                }

                if (client is OpcDaClient daClient)
                {
                    daClient.Warning += message =>
                        logger_.LogWarning("OPC DA source {SourceId}: {Message}", source.SourceId, message);
                }

                sessions[source.SourceId] = new SourceSession(source, client);
                bridge_state_.SetSourceConnectionState(source.SourceId, "Connected");

                if (client is OpcDaClient connectedDaClient)
                {
                    bridge_state_.SetSourceServerInfo(
                        source.SourceId,
                        connectedDaClient.ServerInfo?.Describe() ?? string.Empty);
                }

                changed.Add(source.SourceId);
                watchdog_activity_.TryRemove(source.SourceId, out _);

                if (client is OpcUaSourceClient uaClient)
                {
                    SourceMappingCache? cache = source_mapping_cache_;
                    if (cache is not null)
                    {
                        IReadOnlyList<TagMapping> desired = cache.GetSourceReadMappings(source.SourceId);
                        await uaClient.ReconcileMonitoredItemsAsync(desired, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (SourceConnectionLostException ex)
            {
                // Server unreachable / channel dead — transient. Do not create a session
                // (no poller) and report "Reconnecting": the coordinator retries with
                // backoff on the next tick until the server is reachable again.
                bridge_state_.SetSourceConnectionState(source.SourceId, "Reconnecting");
                bridge_state_.SetSourceError(source.SourceId, ex);
                logger_.LogWarning(ex, "Source {SourceId} connection lost; will retry", source.SourceId);
                connectionFailures = true;
                watchdog_activity_.TryRemove(source.SourceId, out _);
            }
            catch (Exception ex)
            {
                bridge_state_.SetSourceConnectionState(source.SourceId, "Faulted");
                bridge_state_.SetSourceError(source.SourceId, ex);
                logger_.LogWarning(ex, "Source {SourceId} connection failed", source.SourceId);
                changed.Add(source.SourceId);
            }
        }

        return (changed, connectionFailures);
    }

    internal static bool SourceConnectionEquals(DaSourceRuntimeSettings a, DaSourceRuntimeSettings b)
    {
        if (!string.Equals(a.SourceType, b.SourceType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(a.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
        {
            // UpdateRateMs is connection identity for UA sources: it drives the
            // subscription's PublishingInterval, which is fixed when the client is
            // created. A rate change must recreate the session so a new subscription
            // (and its publishing cadence) is built — otherwise the API reports
            // success but values keep arriving at the old interval.
            return string.Equals(a.EndpointUrl, b.EndpointUrl, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.SecurityMode, b.SecurityMode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.SecurityPolicy, b.SecurityPolicy, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.UaUsername, b.UaUsername, StringComparison.Ordinal)
                && string.Equals(a.UaPassword, b.UaPassword, StringComparison.Ordinal)
                && a.SessionTimeoutMs == b.SessionTimeoutMs
                && a.ReconnectDelayMs == b.ReconnectDelayMs
                && a.UpdateRateMs == b.UpdateRateMs;
        }

        if (string.Equals(a.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(a.Transport, b.Transport, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.SerialPortName, b.SerialPortName, StringComparison.OrdinalIgnoreCase)
                && a.BaudRate == b.BaudRate
                && a.DataBits == b.DataBits
                && string.Equals(a.Parity, b.Parity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.StopBits, b.StopBits, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.StationNo, b.StationNo, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.PcNo, b.PcNo, StringComparison.OrdinalIgnoreCase)
                && a.TimeoutMs == b.TimeoutMs
                && a.RetryCount == b.RetryCount;
        }

        if (string.Equals(a.SourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(a.Transport, b.Transport, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.SerialPortName, b.SerialPortName, StringComparison.OrdinalIgnoreCase)
                && a.BaudRate == b.BaudRate
                && a.DataBits == b.DataBits
                && string.Equals(a.Parity, b.Parity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.StopBits, b.StopBits, StringComparison.OrdinalIgnoreCase)
                && a.LocalPpiAddress == b.LocalPpiAddress
                && a.RemotePpiAddress == b.RemotePpiAddress
                && a.TimeoutMs == b.TimeoutMs
                && a.RetryCount == b.RetryCount;
        }

        if (string.Equals(a.SourceType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
        {
            return a.LogicalStationNumber == b.LogicalStationNumber
                && a.MxComponentTimeoutMs == b.MxComponentTimeoutMs
                && a.MxComponentRetryCount == b.MxComponentRetryCount;
        }

        // OPC DA
        return string.Equals(a.ProgId, b.ProgId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.RemoteUsername, b.RemoteUsername, StringComparison.Ordinal)
            && string.Equals(a.RemotePassword, b.RemotePassword, StringComparison.Ordinal)
            && string.Equals(a.RemoteDomain, b.RemoteDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SourceSettingsEquals(DaSourceRuntimeSettings a, DaSourceRuntimeSettings b)
        => a.UpdateRateMs == b.UpdateRateMs
            && a.UseSubscriptions == b.UseSubscriptions
            && a.MaxMappedTags == b.MaxMappedTags
            && string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal)
            && SourceConnectionEquals(a, b);

    private async Task ReconcileUaMonitoredItemsAsync(
        Dictionary<string, SourceSession> sessions,
        SourceMappingCache cache,
        CancellationToken cancellationToken)
    {
        foreach ((string sourceId, SourceSession session) in sessions)
        {
            if (session.Client is not OpcUaSourceClient uaClient)
            {
                continue;
            }

            try
            {
                IReadOnlyList<TagMapping> desired = cache.GetSourceReadMappings(sourceId);
                await uaClient.ReconcileMonitoredItemsAsync(desired, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Reconcile itself degrades to poll; log and keep other sources moving.
                logger_.LogWarning(
                    ex,
                    "UA MonitoredItem reconcile failed for source {SourceId}",
                    sourceId);
            }
        }
    }

    private void OnSubscriptionValues(IReadOnlyList<BridgeValue> values)
    {
        if (values.Count > 0)
        {
            watchdog_activity_[values[0].SourceId] = DateTime.UtcNow;
            bridge_state_.UpdateDaRead(values[0].SourceId, values, TimeSpan.Zero);
        }
        else
        {
            bridge_state_.UpdateDaRead(string.Empty, values, TimeSpan.Zero);
        }
        SourceMappingCache? cache = source_mapping_cache_;
        for (int i = 0; i < values.Count; i++)
        {
            BridgeValue value = values[i];
            bridge_state_.SetValue(value);
            ua_server_.UpdateValue(value);
            if (cache is not null)
            {
                ForwardToConsumers(value, cache, CancellationToken.None);
            }
        }
    }

    private static string NormalizeKey(string sourceId, string itemId)
    {
        return string.Concat(sourceId.Trim(), "::", itemId.Trim());
    }

    private void OnBridgeValueUpdated(BridgeValue value)
    {
        string key = NormalizeKey(value.SourceId, value.ItemId);
        if (mqtt_enabled_keys_.Contains(key))
        {
            _ = mqtt_publish_channel_.Writer.WriteAsync(value);
        }
        if (influx_enabled_keys_.Contains(key))
        {
            _ = influx_write_channel_.Writer.WriteAsync(value);
        }
    }

    private async Task ConnectMqttAsync(CancellationToken ct)
    {
        try
        {
            mqtt_settings_.SetState("Connecting");
            await mqtt_bridge_.ConnectAsync(mqtt_settings_.GetOptions(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            mqtt_settings_.SetState("Faulted", ex.Message);
            logger_.LogWarning(ex, "MQTT connect failed");
        }
    }

    private async Task MqttPublishDrainAsync(CancellationToken ct)
    {
        MqttBrokerOptions options = mqtt_settings_.GetOptions();
        await foreach (BridgeValue value in mqtt_publish_channel_.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                options = mqtt_settings_.GetOptions();
                string topic = MqttPayload.BuildTopic(options, value.SourceId, value.ItemId,
                    ResolveMqttTopicOverride(value.SourceId, value.ItemId));
                string payload = MqttPayload.Serialize(value, options.PayloadFields, ResolveDisplayName(value.SourceId, value.ItemId));
                await mqtt_bridge_.PublishAsync(topic, payload, ct).ConfigureAwait(false);
                mqtt_settings_.IncrementPublished();
                mqtt_values_.Set("PUB", topic, payload);
            }
            catch (Exception ex)
            {
                logger_.LogWarning(ex, "MQTT publish failed for {SourceId}/{ItemId}", value.SourceId, value.ItemId);
            }
        }
    }

    private async Task ConnectInfluxAsync(CancellationToken ct)
    {
        try
        {
            influx_settings_.SetState("Connecting");
            await influx_writer_.ConnectAsync(influx_settings_.GetOptions(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            influx_settings_.SetState("Faulted", ex.Message);
            logger_.LogWarning(ex, "Influx connect failed");
        }
    }

    private async Task InfluxWriteDrainAsync(CancellationToken ct)
    {
        await foreach (BridgeValue value in influx_write_channel_.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                InfluxOptions options = influx_settings_.GetOptions();
                if (!options.Enabled || influx_writer_.State != InfluxConnectionState.Connected)
                {
                    continue;
                }

                await influx_writer_.WritePointAsync(
                    value,
                    ResolveDisplayName(value.SourceId, value.ItemId),
                    ct).ConfigureAwait(false);
                influx_settings_.IncrementWritten();
            }
            catch (Exception ex)
            {
                logger_.LogWarning(ex, "Influx write failed for {SourceId}/{ItemId}", value.SourceId, value.ItemId);
            }
        }
    }

    private async Task OnMqttInboundAsync(MqttInboundMessage message)
    {
        mqtt_settings_.IncrementReceived();
        mqtt_values_.Set("SUB", message.Topic, message.RawValue);

        (string? sourceId, string? itemId) = ResolveTopicToMapping(message.Topic);
        if (sourceId is null || itemId is null)
        {
            logger_.LogDebug("MQTT inbound topic has no matching mapping: {Topic}", message.Topic);
            return;
        }

        var (mappings, _) = mapping_store_.GetSnapshot();
        TagMapping? mapping = mappings.FirstOrDefault(m =>
            string.Equals(m.SourceId, sourceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
        if (mapping is null || !mapping.MqttEnabled)
        {
            return;
        }

        object? converted = ConvertIncoming(mapping, message.RawValue);
        bool ok = await ApplyUaWriteAsync(sourceId, itemId, converted, message.TimestampUtc ?? DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
        if (!ok)
        {
            logger_.LogDebug("MQTT inbound write rejected for {SourceId}/{ItemId}", sourceId, itemId);
        }
    }

    private (string? SourceId, string? ItemId) ResolveTopicToMapping(string topic)
    {
        MqttBrokerOptions options = mqtt_settings_.GetOptions();
        string prefix = (string.IsNullOrWhiteSpace(options.TopicPrefix) ? "bridge/tags" : options.TopicPrefix.Trim().Trim('/')) + "/";
        if (!topic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (null, null);

        string remainder = topic[prefix.Length..];
        int slash = remainder.IndexOf('/');
        if (slash < 0) return (null, null);
        return (remainder[..slash], remainder[(slash + 1)..]);
    }

    private string? ResolveMqttTopicOverride(string sourceId, string itemId)
    {
        var (mappings, _) = mapping_store_.GetSnapshot();
        TagMapping? mapping = mappings.FirstOrDefault(m =>
            string.Equals(m.SourceId, sourceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
        return mapping?.MqttTopic;
    }

    private string? ResolveDisplayName(string sourceId, string itemId)
    {
        var (mappings, _) = mapping_store_.GetSnapshot();
        TagMapping? mapping = mappings.FirstOrDefault(m =>
            string.Equals(m.SourceId, sourceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
        return mapping?.DisplayName;
    }

    private static object? ConvertIncoming(TagMapping mapping, string? rawValue)
    {
        if (rawValue is null) return null;
        string text = rawValue.Trim();
        if (string.Equals(mapping.DataType, "String", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        if (bool.TryParse(text, out bool b)) return b;
        if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long l)) return l;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
        return text;
    }

    /// <summary>Write a value through the existing UA write path (WriteQueue → per-source consumer → DA). Same seam a UA client write uses.</summary>
    public async Task<bool> ApplyUaWriteAsync(string sourceId, string itemId, object? value, DateTime timestampUtc, CancellationToken ct)
    {
        if (write_queue_ is null) return false;

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        write_queue_.Enqueue(sourceId, new WriteRequest(sourceId, itemId, value, tcs));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetResult(false);
            return false;
        }
    }

    /// <summary>
    /// Tags whose monitored-item create failed and are being auto-retried (source-side
    /// disconnect signal for the dashboard's per-tag Disc badge).
    /// </summary>
    public IReadOnlyList<DisconnectedTag> GetDisconnectedTags()
    {
        Dictionary<string, SourceSession>? sessions = active_sessions_;
        List<DisconnectedTag> result = new();
        if (sessions is null)
        {
            return result;
        }

        foreach ((string sourceId, SourceSession session) in sessions)
        {
            if (session.Client is not OpcUaSourceClient uaClient)
            {
                continue;
            }

            foreach (string itemId in uaClient.GetFailedItemIds())
            {
                result.Add(new DisconnectedTag(sourceId, itemId));
            }
        }

        return result;
    }

    public async Task<(bool Ok, string? Error)> TryHmiWriteAsync(
        string sourceId,
        string itemId,
        object? value,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(itemId))
        {
            return (false, "sourceId and itemId are required");
        }

        (IReadOnlyList<TagMapping> mappings, _) = mapping_store_.GetSnapshot();
        TagMapping? mapping = mappings.FirstOrDefault(m =>
            string.Equals(m.SourceId, sourceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.ItemId, itemId, StringComparison.OrdinalIgnoreCase));

        if (mapping is null)
        {
            return (false, "Tag is not mapped");
        }

        if (!mapping.Enabled)
        {
            return (false, "Tag is disabled");
        }

        if (!mapping.Writeable)
        {
            return (false, "Tag is read-only");
        }

        if (write_queue_ is null)
        {
            return (false, "Bridge write path is not ready");
        }

        object? converted = ConvertHmiValue(mapping, value);
        bool ok = await ApplyUaWriteAsync(mapping.SourceId, mapping.ItemId, converted, DateTime.UtcNow, ct)
            .ConfigureAwait(false);
        return ok ? (true, null) : (false, "Write failed or timed out");
    }

    private static object? ConvertHmiValue(TagMapping mapping, object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonElement je)
        {
            value = je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Number => je.TryGetInt64(out long l) ? l : je.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => je.ToString()
            };
        }

        if (value is string s)
        {
            return ConvertIncoming(mapping, s);
        }

        return value;
    }
    public object GetDiagnostics()
    {
        // STA thread health per source
        List<object> staThreads = new();
        Dictionary<string, SourceSession>? sessions = active_sessions_;
        if (sessions is not null)
        {
            foreach ((string sourceId, SourceSession session) in sessions)
            {
                if (session.Client is OpcDaClient daClient)
                {
                    var stats = daClient.GetStaThreadStats();
                    staThreads.Add(new
                    {
                        sourceId,
                        alive = stats?.Alive ?? false,
                        queuedItems = stats?.QueuedItems ?? 0,
                        lastActionUtc = stats?.LastActionUtc
                    });
                }
            }
        }

        // Write queue stats
        object? writeQueue = null;
        if (write_queue_ is not null)
        {
            var (depth, enqueued, succeeded, failed) = write_queue_.GetStats();
            writeQueue = new
            {
                currentDepth = depth,
                totalEnqueued = enqueued,
                totalSucceeded = succeeded,
                totalFailed = failed
            };
        }

        // UA bandwidth estimate (from BridgeNodeManager notification counter)
        var (totalNotifications, notificationsPerSec) = ua_server_.GetBandwidthEstimate();

        return new
        {
            staThreads,
            writeQueue,
            uaBandwidth = new
            {
                totalNotifications,
                notificationsPerSec,
                estimatedBytesPerSec = notificationsPerSec * 80.0
            }
        };
    }

    public bool TryResolve(string sourceId, string itemId, out DaTagMetadata metadata)
    {
        metadata = new DaTagMetadata(null, null);

        Dictionary<string, SourceSession>? sessions = active_sessions_;
        if (sessions is null || string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        string normalizedSourceId = NormalizeSourceId(sourceId);
        if (!sessions.TryGetValue(normalizedSourceId, out SourceSession? session))
        {
            return false;
        }

        if (!session.Client.TryGetTagMetadata(itemId.Trim(), out short? canonicalDataType, out int? accessRights))
        {
            return false;
        }

        metadata = new DaTagMetadata(canonicalDataType, accessRights);
        return true;
    }

    private static string NormalizeSourceId(string? sourceId)
    {
        string value = sourceId?.Trim() ?? string.Empty;
        return value.Length == 0 ? DaRuntimeSettings.DefaultSourceId : value;
    }



    private static async Task DisposeSessionsAsync(Dictionary<string, SourceSession> sessions)
    {
        foreach (SourceSession session in sessions.Values)
        {
            await session.Client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool TryCreateManualValue(TagMapping mapping, out BridgeValue value)
    {
        if (TryConvertManualValue(mapping.DataType, mapping.ManualValue, out object? convertedValue))
        {
            value = new BridgeValue(
                mapping.SourceId,
                mapping.ItemId,
                convertedValue,
                DateTime.UtcNow,
                192,
                true);
            return true;
        }

        value = new BridgeValue(mapping.SourceId, mapping.ItemId, null, DateTime.UtcNow, 0, false);
        return false;
    }

    private static bool TryConvertManualValue(string dataType, string? manualValue, out object? convertedValue)
    {
        string text = manualValue?.Trim() ?? string.Empty;
        string normalizedDataType = dataType.Trim().ToUpperInvariant();

        if (normalizedDataType is "STRING")
        {
            convertedValue = text;
            return true;
        }

        if (normalizedDataType is "BOOL" or "BOOLEAN")
        {
            if (bool.TryParse(text, out bool boolValue))
            {
                convertedValue = boolValue;
                return true;
            }

            if (text == "1")
            {
                convertedValue = true;
                return true;
            }

            if (text == "0")
            {
                convertedValue = false;
                return true;
            }

            convertedValue = null;
            return false;
        }

        if (normalizedDataType is "BYTE")
        {
            if (byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte byteValue))
            {
                convertedValue = byteValue;
                return true;
            }
        }
        else if (normalizedDataType is "SBYTE")
        {
            if (sbyte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte sbyteValue))
            {
                convertedValue = sbyteValue;
                return true;
            }
        }
        else if (normalizedDataType is "INT16" or "SHORT")
        {
            if (short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out short shortValue))
            {
                convertedValue = shortValue;
                return true;
            }
        }
        else if (normalizedDataType is "UINT16")
        {
            if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort ushortValue))
            {
                convertedValue = ushortValue;
                return true;
            }
        }
        else if (normalizedDataType is "INT32" or "INT")
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
            {
                convertedValue = intValue;
                return true;
            }
        }
        else if (normalizedDataType is "UINT32")
        {
            if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint uintValue))
            {
                convertedValue = uintValue;
                return true;
            }
        }
        else if (normalizedDataType is "INT64" or "LONG")
        {
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
            {
                convertedValue = longValue;
                return true;
            }
        }
        else if (normalizedDataType is "UINT64")
        {
            if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong ulongValue))
            {
                convertedValue = ulongValue;
                return true;
            }
        }
        else if (normalizedDataType is "FLOAT" or "SINGLE")
        {
            if (float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float floatValue))
            {
                convertedValue = floatValue;
                return true;
            }
        }
        else if (normalizedDataType is "DOUBLE" or "REAL8")
        {
            if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double doubleValue))
            {
                convertedValue = doubleValue;
                return true;
            }
        }
        else if (normalizedDataType is "DECIMAL")
        {
            if (decimal.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out decimal decimalValue))
            {
                convertedValue = decimalValue;
                return true;
            }
        }
        else if (TryInferManualValue(text, out object? inferredValue))
        {
            convertedValue = inferredValue;
            return true;
        }

        convertedValue = null;
        return false;
    }

    private static bool TryInferManualValue(string text, out object? convertedValue)
    {
        if (bool.TryParse(text, out bool boolValue))
        {
            convertedValue = boolValue;
            return true;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
        {
            convertedValue = longValue;
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double doubleValue))
        {
            convertedValue = doubleValue;
            return true;
        }

        convertedValue = text;
        return true;
    }

    public sealed record DisconnectedTag(string SourceId, string ItemId);

    internal sealed class SourceMappingCache
    {
        private static readonly IReadOnlyList<TagMapping> EmptyMappings = Array.Empty<TagMapping>();
        private readonly Dictionary<string, SourceMappingSet> mappings_by_source_;
        private readonly IReadOnlyList<TagMapping> active_mappings_;
        private readonly Dictionary<string, IReadOnlyList<TagMapping>> consumers_by_provider_;

        private SourceMappingCache(
            Dictionary<string, SourceMappingSet> mappingsBySource,
            IReadOnlyList<TagMapping> activeMappings,
            Dictionary<string, IReadOnlyList<TagMapping>> consumersByProvider)
        {
            mappings_by_source_ = mappingsBySource;
            active_mappings_ = activeMappings;
            consumers_by_provider_ = consumersByProvider;
        }

        public static SourceMappingCache Build(IReadOnlyList<TagMapping> mappings)
        {
            return Build(mappings, Array.Empty<DaLinkRule>());
        }

        public static SourceMappingCache Build(IReadOnlyList<TagMapping> mappings, IReadOnlyList<DaLinkRule> rules)
        {
            Dictionary<string, List<TagMapping>> groupedMappings = new(StringComparer.OrdinalIgnoreCase);
            List<TagMapping> activeMappings = new(mappings.Count);
            Dictionary<string, TagMapping> mappingsByKey = new(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < mappings.Count; i++)
            {
                TagMapping mapping = mappings[i];
                if (!groupedMappings.TryGetValue(mapping.SourceId, out List<TagMapping>? sourceMappings))
                {
                    sourceMappings = new List<TagMapping>();
                    groupedMappings[mapping.SourceId] = sourceMappings;
                }

                sourceMappings.Add(mapping);
                mappingsByKey[GetMappingKey(mapping.SourceId, mapping.ItemId)] = mapping;

                if (mapping.Enabled)
                {
                    activeMappings.Add(mapping);
                }
            }

            Dictionary<string, List<TagMapping>> consumersByProvider = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rules.Count; i++)
            {
                DaLinkRule rule = rules[i];
                if (!rule.Enabled)
                {
                    continue;
                }

                if (!mappingsByKey.TryGetValue(GetMappingKey(rule.ConsumerSourceId, rule.ConsumerItemId), out TagMapping? consumer) ||
                    !consumer.Enabled)
                {
                    continue;
                }

                string providerKey = GetMappingKey(rule.ProviderSourceId, rule.ProviderItemId);
                if (!consumersByProvider.TryGetValue(providerKey, out List<TagMapping>? consumers))
                {
                    consumers = new List<TagMapping>();
                    consumersByProvider[providerKey] = consumers;
                }

                consumers.Add(consumer);
            }

            Dictionary<string, SourceMappingSet> frozenMappings = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string sourceId, List<TagMapping> sourceMappings) in groupedMappings)
            {
                TagMapping[] all = sourceMappings.ToArray();
                TagMapping[] active = sourceMappings.Where(mapping => mapping.Enabled).ToArray();
                TagMapping[] sourceRead = active.Where(mapping =>
                    string.Equals(mapping.Mode, TagMode.Source, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mapping.AccessRights, TagAccessRights.Write, StringComparison.OrdinalIgnoreCase)).ToArray();
                TagMapping[] manual = active.Where(mapping =>
                    string.Equals(mapping.Mode, TagMode.Manual, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mapping.AccessRights, TagAccessRights.Write, StringComparison.OrdinalIgnoreCase)).ToArray();
                frozenMappings[sourceId] = new SourceMappingSet(all, active, sourceRead, manual);
            }

            Dictionary<string, IReadOnlyList<TagMapping>> frozenConsumers = consumersByProvider
                .ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<TagMapping>)kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

            return new SourceMappingCache(frozenMappings, activeMappings.ToArray(), frozenConsumers);
        }

        public IReadOnlyList<TagMapping> GetActiveMappings()
        {
            return active_mappings_;
        }

        public IReadOnlyList<TagMapping> GetMappings(string sourceId)
        {
            return mappings_by_source_.TryGetValue(sourceId, out SourceMappingSet? mappings)
                ? mappings.All
                : EmptyMappings;
        }

        public IReadOnlyList<TagMapping> GetSourceReadMappings(string sourceId)
        {
            return mappings_by_source_.TryGetValue(sourceId, out SourceMappingSet? mappings)
                ? mappings.SourceRead
                : EmptyMappings;
        }

        public IReadOnlyList<TagMapping> GetManualMappings(string sourceId)
        {
            return mappings_by_source_.TryGetValue(sourceId, out SourceMappingSet? mappings)
                ? mappings.Manual
                : EmptyMappings;
        }

        public IReadOnlyList<int> GetDistinctRates(string sourceId, int defaultRate)
        {
            if (!mappings_by_source_.TryGetValue(sourceId, out SourceMappingSet? mappings))
            {
                return [defaultRate];
            }

            HashSet<int> rates = new();
            for (int i = 0; i < mappings.SourceRead.Count; i++)
            {
                rates.Add(mappings.SourceRead[i].PollRateMs > 0 ? mappings.SourceRead[i].PollRateMs : defaultRate);
            }

            return rates.Count > 0 ? rates.ToArray() : new[] { defaultRate };
        }

        public IReadOnlyList<TagMapping> GetSourceReadMappingsByRate(string sourceId, int rate, int defaultRate)
        {
            if (!mappings_by_source_.TryGetValue(sourceId, out SourceMappingSet? mappings))
            {
                return EmptyMappings;
            }

            return mappings.SourceRead
                .Where(m => (m.PollRateMs > 0 ? m.PollRateMs : defaultRate) == rate)
                .ToArray();
        }
        /// <summary>
        /// Returns the consumer tags linked to the given provider tag (SourceId::ItemId).
        /// Empty when nothing links to it. Used to forward a provider's value into its consumers.
        /// </summary>
        public IReadOnlyList<TagMapping> GetConsumersByProvider(string providerSourceId, string providerItemId)
        {
            return consumers_by_provider_.TryGetValue(GetMappingKey(providerSourceId, providerItemId), out IReadOnlyList<TagMapping>? consumers)
                ? consumers
                : EmptyMappings;
        }

        private static string GetMappingKey(string sourceId, string itemId)
        {
            return string.Concat(sourceId.Trim(), "::", itemId.Trim());
        }
    }

    private sealed record SourceMappingSet(
        IReadOnlyList<TagMapping> All,
        IReadOnlyList<TagMapping> Active,
        IReadOnlyList<TagMapping> SourceRead,
        IReadOnlyList<TagMapping> Manual);

    private sealed record SourcePollResult(string SourceId, bool ReadSucceeded, int OutputValueCount)
    {
        public static SourcePollResult Success(string sourceId, int outputValueCount)
        {
            return new SourcePollResult(sourceId, true, outputValueCount);
        }

        public static SourcePollResult Failure(string sourceId, int outputValueCount)
        {
            return new SourcePollResult(sourceId, false, outputValueCount);
        }
    }

    private sealed class SharedCacheHolder
    {
        public volatile SourceMappingCache Cache;

        public SharedCacheHolder(SourceMappingCache cache)
        {
            Cache = cache;
        }
    }

    internal sealed class SourceSession
    {
        public SourceSession(DaSourceRuntimeSettings source, ISourceClient client)
        {
            Source = source;
            Client = client;
        }

        public DaSourceRuntimeSettings Source { get; }
        public ISourceClient Client { get; }
        public CancellationTokenSource? PollerCts { get; set; }
    }
}
