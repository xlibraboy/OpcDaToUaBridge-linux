using System.Diagnostics;
using System.Globalization;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Ua;

namespace OpcBridge.App;

public sealed class BridgeWorker : BackgroundService
{
    private const int CoordinatorTickMs = 200;

    private readonly UaServerHost ua_server_;
    private readonly BridgeState bridge_state_;
    private readonly MappingStore mapping_store_;
    private readonly DaRuntimeSettings da_settings_;
    private readonly DaClientFactory da_client_factory_;
    private readonly ILogger<BridgeWorker> logger_;
    private readonly IReadOnlyDictionary<int, int> rate_limits_;

    public BridgeWorker(
        UaServerHost uaServer,
        BridgeState bridgeState,
        MappingStore mappingStore,
        DaRuntimeSettings daSettings,
        DaClientFactory daClientFactory,
        IOptions<BridgeOptions> bridgeOptions,
        ILogger<BridgeWorker> logger)
    {
        ua_server_ = uaServer;
        bridge_state_ = bridgeState;
        mapping_store_ = mappingStore;
        da_settings_ = daSettings;
        da_client_factory_ = daClientFactory;
        logger_ = logger;
        rate_limits_ = bridgeOptions.Value.RateLimits;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DaRuntimeSettingsSnapshot settings = da_settings_.GetSnapshot();
        (IReadOnlyList<TagMapping> mappings, long mappingVersion) = mapping_store_.GetSnapshot();
        SourceMappingCache sourceMappingCache = SourceMappingCache.Build(mappings);
        IReadOnlyList<TagMapping> activeMappings = sourceMappingCache.GetActiveMappings();
        bridge_state_.Configure(settings.UpdateRateMs, activeMappings.Count, settings.Sources);

        try
        {
            logger_.LogInformation("Starting bridge with {MappingCount} mappings across {SourceCount} sources", activeMappings.Count, settings.Sources.Count);
            await ua_server_.StartAsync(activeMappings, stoppingToken).ConfigureAwait(false);

            long uaMappingVersion = mappingVersion;
            long connectedVersion = -1;
            Dictionary<string, SourceSession> sessions = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Task> pollers = new(StringComparer.OrdinalIgnoreCase);
            CancellationTokenSource pollerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            SharedCacheHolder cacheHolder = new(sourceMappingCache);
            ConcurrentQueue<string> failedSourceQueue = new();

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    settings = da_settings_.GetSnapshot();
                    (mappings, mappingVersion) = mapping_store_.GetSnapshot();

                    try
                    {
                        if (!failedSourceQueue.IsEmpty)
                        {
                            failedSourceQueue.Clear();
                            await StopPollersAsync(pollers, pollerCts).ConfigureAwait(false);
                            pollerCts.Dispose();
                            await DisposeSessionsAsync(sessions).ConfigureAwait(false);
                            sessions.Clear();
                            connectedVersion = -1;
                            pollerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        }

                        if (mappingVersion != uaMappingVersion)
                        {
                            cacheHolder.Cache = SourceMappingCache.Build(mappings);
                            activeMappings = cacheHolder.Cache.GetActiveMappings();
                            ua_server_.SyncMappings(activeMappings);
                            bridge_state_.RetainMappedValues(activeMappings);
                            uaMappingVersion = mappingVersion;
                            connectedVersion = -1;
                            bridge_state_.UpdateSources(settings.UpdateRateMs, activeMappings.Count, settings.Sources);
                            logger_.LogInformation("Applied tag mapping change: {Count} mappings", activeMappings.Count);
                        }

                        if (connectedVersion != settings.Version)
                        {
                            bridge_state_.ClearRateGroups();
                            await StopPollersAsync(pollers, pollerCts).ConfigureAwait(false);
                            pollerCts.Dispose();
                            await ReconfigureSessionsAsync(settings, sessions, stoppingToken).ConfigureAwait(false);
                            connectedVersion = settings.Version;
                            bridge_state_.UpdateSources(settings.UpdateRateMs, activeMappings.Count, settings.Sources);
                            pollerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            StartPollers(settings, sessions, cacheHolder, failedSourceQueue, pollers, pollerCts.Token);
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
                        await StopPollersAsync(pollers, pollerCts).ConfigureAwait(false);
                        pollerCts.Dispose();
                        await DisposeSessionsAsync(sessions).ConfigureAwait(false);
                        sessions.Clear();
                        connectedVersion = -1;
                        pollerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        await Task.Delay(settings.UpdateRateMs, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await StopPollersAsync(pollers, pollerCts).ConfigureAwait(false);
                pollerCts.Dispose();
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
        CancellationToken pollerToken)
    {
        SourceMappingCache cache = cacheHolder.Cache;

        for (int i = 0; i < settings.Sources.Count; i++)
        {
            DaSourceRuntimeSettings source = settings.Sources[i];
            if (!sessions.TryGetValue(source.SourceId, out SourceSession? session))
            {
                continue;
            }

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
        }
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

    private static async Task StopPollersAsync(Dictionary<string, Task> pollers, CancellationTokenSource? pollerCts)
    {
        try { pollerCts?.Cancel(); } catch (ObjectDisposedException) { }
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
                // Poller exceptions are logged within the poller; suppress during teardown.
            }
        }
    }

    private int GetRateLimit(int rateMs)
    {
        return rate_limits_.TryGetValue(rateMs, out int limit) ? limit : 0;
    }

    private async Task<SourcePollResult> PollSourceAsync(
        DaSourceRuntimeSettings source,
        SourceSession session,
        IReadOnlyList<TagMapping> sourceReadMappings,
        IReadOnlyList<TagMapping> manualMappings,
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
            }

            sourceReadSucceeded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
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
                bridge_state_.ClearValue(mapping.SourceId, mapping.DaItemId);
                continue;
            }

            bridge_state_.SetValue(manualValue);
            ua_server_.UpdateValue(manualValue);
            updatedCount++;
        }

        return updatedCount;
    }

    private void ClearReadValues(IReadOnlyList<TagMapping> readMappings)
    {
        for (int i = 0; i < readMappings.Count; i++)
        {
            TagMapping mapping = readMappings[i];
            bridge_state_.ClearValue(mapping.SourceId, mapping.DaItemId);
        }
    }

    private async Task ReconfigureSessionsAsync(
        DaRuntimeSettingsSnapshot settings,
        Dictionary<string, SourceSession> sessions,
        CancellationToken cancellationToken)
    {
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
        }

        for (int i = 0; i < settings.Sources.Count; i++)
        {
            DaSourceRuntimeSettings source = settings.Sources[i];

            if (sessions.Remove(source.SourceId, out SourceSession? existing))
            {
                await existing.Client.DisposeAsync().ConfigureAwait(false);
            }

            bridge_state_.SetSourceConnectionState(source.SourceId, "Connecting");
            IDaClient client = da_client_factory_.Create(settings, source);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            sessions[source.SourceId] = new SourceSession(source, client);
            bridge_state_.SetSourceConnectionState(source.SourceId, "Connected");
        }
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
                mapping.DaItemId,
                convertedValue,
                DateTime.UtcNow,
                192,
                true);
            return true;
        }

        value = new BridgeValue(mapping.SourceId, mapping.DaItemId, null, DateTime.UtcNow, 0, false);
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

    private sealed class SourceMappingCache
    {
        private static readonly IReadOnlyList<TagMapping> EmptyMappings = Array.Empty<TagMapping>();
        private readonly Dictionary<string, SourceMappingSet> mappings_by_source_;
        private readonly IReadOnlyList<TagMapping> active_mappings_;

        private SourceMappingCache(Dictionary<string, SourceMappingSet> mappingsBySource, IReadOnlyList<TagMapping> activeMappings)
        {
            mappings_by_source_ = mappingsBySource;
            active_mappings_ = activeMappings;
        }

        public static SourceMappingCache Build(IReadOnlyList<TagMapping> mappings)
        {
            Dictionary<string, List<TagMapping>> groupedMappings = new(StringComparer.OrdinalIgnoreCase);
            List<TagMapping> activeMappings = new(mappings.Count);

            for (int i = 0; i < mappings.Count; i++)
            {
                TagMapping mapping = mappings[i];
                if (!groupedMappings.TryGetValue(mapping.SourceId, out List<TagMapping>? sourceMappings))
                {
                    sourceMappings = new List<TagMapping>();
                    groupedMappings[mapping.SourceId] = sourceMappings;
                }

                sourceMappings.Add(mapping);
                if (mapping.Enabled)
                {
                    activeMappings.Add(mapping);
                }
            }

            Dictionary<string, SourceMappingSet> frozenMappings = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string sourceId, List<TagMapping> sourceMappings) in groupedMappings)
            {
                TagMapping[] all = sourceMappings.ToArray();
                TagMapping[] active = sourceMappings.Where(mapping => mapping.Enabled).ToArray();
                TagMapping[] sourceRead = active.Where(mapping => string.Equals(mapping.Mode, TagMode.Source, StringComparison.OrdinalIgnoreCase)).ToArray();
                TagMapping[] manual = active.Where(mapping => string.Equals(mapping.Mode, TagMode.Manual, StringComparison.OrdinalIgnoreCase)).ToArray();
                frozenMappings[sourceId] = new SourceMappingSet(all, active, sourceRead, manual);
            }

            return new SourceMappingCache(frozenMappings, activeMappings.ToArray());
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

    private sealed record SourceSession(DaSourceRuntimeSettings Source, IDaClient Client);
}
