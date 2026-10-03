using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpcBridge.Core;

namespace OpcBridge.App;

public sealed class BridgeState
{
    private readonly ConcurrentDictionary<string, BridgeValueSnapshot> values_by_key_;
    public event Action<BridgeValue>? ValueUpdated;
    private readonly Dictionary<string, RateGroupStatus> rate_groups_ = new(StringComparer.OrdinalIgnoreCase);
    private BridgeRuntimeStatus status_ = BridgeRuntimeStatus.Empty;
    private readonly object status_lock_ = new();
    private readonly ConcurrentDictionary<string, InterlinkStats> link_stats_ = new(StringComparer.OrdinalIgnoreCase);

    // Measured source-value flow for Diagnostics ▸ Values/sec: a monotonic total plus a
    // ≈1-second window, mirroring the UA bandwidth window (BridgeNodeManager). Empty poll
    // passes and quiet subscriptions leave the rate to decay instead of reporting a 0.
    private long source_values_total_;
    private long value_window_start_ticks_ = DateTime.UtcNow.Ticks;
    private long value_window_count_;

    public BridgeState(IOptions<BridgeOptions> options)
    {
        int expectedTagCount = options?.Value.ExpectedTagCount ?? 1000;
        int capacity = Math.Max(64, expectedTagCount);
        int concurrencyLevel = Math.Max(1, Environment.ProcessorCount * 2);
        values_by_key_ = new ConcurrentDictionary<string, BridgeValueSnapshot>(
            concurrencyLevel, capacity, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Runtime ports. Set once at startup before any background work begins. </summary>
    public static int HttpPort { get; private set; } = 8080;
    public static int UaPort { get; private set; } = 4840;
    public static bool HttpAutoAssigned { get; private set; }
    public static bool UaAutoAssigned { get; private set; }

    /// <summary>
    /// Address families each chosen port was free on when the bridge started, probed before
    /// it bound its own listeners. A family another process holds is reported, never acted
    /// on: the bridge listens on IPv4 only, so an IPv6-only holder does not stop the bind,
    /// and moving the port would invalidate the installer's build-time firewall rule.
    /// </summary>
    public static PortProbe HttpPortProbe { get; private set; } = new(true, null);
    public static PortProbe UaPortProbe { get; private set; } = new(true, null);

    public static void ConfigurePorts(int httpPort, int uaPort, bool httpAuto, bool uaAuto)
    {
        HttpPort = httpPort;
        UaPort = uaPort;
        HttpAutoAssigned = httpAuto;
        UaAutoAssigned = uaAuto;
    }

    public static void ConfigurePortReport(PortProbe httpProbe, PortProbe uaProbe)
    {
        HttpPortProbe = httpProbe;
        UaPortProbe = uaProbe;
    }

    /// <summary>Windows session the bridge runs in. Set once at startup.</summary>
    public static int SessionId { get; private set; }

    /// <summary>
    /// True when running in a logged-on interactive session. Session-bound PLC
    /// simulators (e.g. GX Simulator shared memory behind MX OPC) are only
    /// reachable from the interactive session, so a session-0 launch must be
    /// surfaced instead of failing silently.
    /// </summary>
    public static bool InteractiveSession { get; private set; } = true;

    public static void ConfigureSession(int sessionId, bool interactive)
    {
        SessionId = sessionId;
        InteractiveSession = interactive;
    }


    public void Configure(int updateRateMs, int mappingCount, IReadOnlyList<DaSourceRuntimeSettings> sources)
    {
        rate_groups_.Clear();
        lock (status_lock_)
        {
            // Live state must survive a status reset. Configure() runs on every
            // reconfigure tick while a source is in retry (or whenever the source
            // set changes); rebuilding snapshots from scratch made healthy sources
            // blink "Disconnected" on every retry tick of an unrelated source —
            // their state is only rewritten by their next poll. Preserve the
            // previous snapshot for sources that are still configured and refresh
            // only the config-derived fields (detection info included).
            Dictionary<string, DaSourceStatusSnapshot> previous = status_.Sources
                .ToDictionary(source => source.SourceId, StringComparer.OrdinalIgnoreCase);
            DaSourceStatusSnapshot[] sourceStatuses = sources
                .Select(source => previous.TryGetValue(source.SourceId, out DaSourceStatusSnapshot? existing)
                    ? existing with
                    {
                        DisplayName = source.DisplayName,
                        Host = source.Host,
                        ProgId = source.ProgId,
                        UpdateRateMs = source.UpdateRateMs,
                        SourceType = source.SourceType,
                        EndpointSummary = BuildEndpointSummary(source)
                    }
                    : BuildDisconnectedSnapshot(source))
                .ToArray();

            // A retry tick must not demote a live bridge to "Starting", and the
            // last-read/last-write counters and bridge error are liveness, not
            // configuration: blanking them while running made the rail blink
            // "just now" / "-" for as long as any source retried. Only a bridge
            // that is actually starting resets them.
            bool wasRunning = string.Equals(status_.BridgeState, "Running", StringComparison.Ordinal);
            status_ = status_ with
            {
                BridgeState = wasRunning ? "Running" : "Starting",
                UpdateRateMs = updateRateMs,
                MappingCount = mappingCount,
                DaConnectionState = AggregateConnectionState(sourceStatuses),
                LastDaReadUtc = wasRunning ? status_.LastDaReadUtc : null,
                LastDaReadCount = wasRunning ? status_.LastDaReadCount : 0,
                LastUaWriteUtc = wasRunning ? status_.LastUaWriteUtc : null,
                LastUaWriteCount = wasRunning ? status_.LastUaWriteCount : 0,
                LastPollDurationMs = wasRunning ? status_.LastPollDurationMs : 0,
                LastPollValueRate = wasRunning ? status_.LastPollValueRate : 0,
                LastError = wasRunning ? status_.LastError : null,
                Sources = sourceStatuses,
                SessionId = SessionId,
                InteractiveSession = InteractiveSession
            };
        }
    }

    public void UpdateSources(int updateRateMs, int mappingCount, IReadOnlyList<DaSourceRuntimeSettings> sources)
    {
        lock (status_lock_)
        {
            Dictionary<string, DaSourceStatusSnapshot> existing = status_.Sources.ToDictionary(source => source.SourceId, StringComparer.OrdinalIgnoreCase);
            DaSourceStatusSnapshot[] merged = new DaSourceStatusSnapshot[sources.Count];

            for (int i = 0; i < sources.Count; i++)
            {
                DaSourceRuntimeSettings source = sources[i];
                if (!existing.TryGetValue(source.SourceId, out DaSourceStatusSnapshot? previous))
                {
                    previous = BuildDisconnectedSnapshot(source);
                }
                else
                {
                    previous = previous with
                    {
                        DisplayName = source.DisplayName,
                        Host = source.Host,
                        ProgId = source.ProgId,
                        UpdateRateMs = source.UpdateRateMs,
                        SourceType = source.SourceType,
                        EndpointSummary = BuildEndpointSummary(source)
                    };
                }

                merged[i] = previous;
            }

            status_ = status_ with
            {
                UpdateRateMs = updateRateMs,
                MappingCount = mappingCount,
                DaConnectionState = AggregateConnectionState(merged),
                Sources = merged
            };
        }
    }


    public void ClearValues()
    {
        values_by_key_.Clear();
    }

    public void ClearSourceValues(string sourceId)
    {
        string prefix = NormalizeKey(sourceId, string.Empty);
        foreach (string key in values_by_key_.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                values_by_key_.TryRemove(key, out _);
            }
        }
    }
    public void SetValue(BridgeValue value)
    {
        // ServerTimestampUtc records when the bridge accepted the value (bridge clock),
        // beside the source-preferred TimestampUtc.
        values_by_key_[NormalizeKey(value.SourceId, value.ItemId)] = new BridgeValueSnapshot(
            value.SourceId,
            value.ItemId,
            value.Value,
            value.TimestampUtc,
            value.DaQuality,
            value.IsGood,
            DateTime.UtcNow);

        ValueUpdated?.Invoke(value);
    }

    public void ClearValue(string sourceId, string itemId)
    {
        values_by_key_.TryRemove(NormalizeKey(sourceId, itemId), out _);
    }

    public void RetainMappedValues(IReadOnlyList<TagMapping> mappings)
    {
        HashSet<string> mappedKeys = mappings
            .Select(mapping => NormalizeKey(mapping.SourceId, mapping.ItemId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string key in values_by_key_.Keys)
        {
            if (!mappedKeys.Contains(key))
            {
                values_by_key_.TryRemove(key, out _);
            }
        }
    }

    public void SetBridgeState(string bridgeState)
    {
        lock (status_lock_)
        {
            status_ = status_ with { BridgeState = bridgeState };
        }
    }

    public void SetDaConnectionState(string connectionState)
    {
        lock (status_lock_)
        {
            status_ = status_ with { DaConnectionState = connectionState };
        }
    }

    public void SetSourceConnectionState(string sourceId, string connectionState)
    {
        lock (status_lock_)
        {
            DaSourceStatusSnapshot[] updated = status_.Sources
                .Select(source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
                    ? source with { ConnectionState = connectionState }
                    : source)
                .ToArray();

            status_ = status_ with
            {
                DaConnectionState = AggregateConnectionState(updated),
                Sources = updated
            };
        }
    }

    public void SetError(Exception exception)
    {
        lock (status_lock_)
        {
            status_ = status_ with
            {
                BridgeState = "Degraded",
                LastError = exception.Message
            };
        }
    }

    public void SetSourceError(string sourceId, Exception exception)
    {
        lock (status_lock_)
        {
            // Error text only — connection state is owned by the caller (e.g. "Reconnecting"
            // for retryable failures must survive setting the error).
            DaSourceStatusSnapshot[] updated = status_.Sources
                .Select(source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
                    ? source with
                    {
                        LastError = exception.Message
                    }
                    : source)
                .ToArray();

            status_ = status_ with
            {
                DaConnectionState = AggregateConnectionState(updated),
                Sources = updated
            };
        }
    }

    public void SetSourceServerInfo(string sourceId, string serverInfo)
    {
        lock (status_lock_)
        {
            DaSourceStatusSnapshot[] updated = status_.Sources
                .Select(source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
                    ? source with { ServerInfo = serverInfo }
                    : source)
                .ToArray();

            status_ = status_ with
            {
                DaConnectionState = AggregateConnectionState(updated),
                Sources = updated
            };
        }
    }

    public void SetSourceReadMode(string sourceId, string readMode)
    {
        lock (status_lock_)
        {
            DaSourceStatusSnapshot[] updated = status_.Sources
                .Select(source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
                    ? source with { ReadMode = readMode }
                    : source)
                .ToArray();

            status_ = status_ with
            {
                DaConnectionState = AggregateConnectionState(updated),
                Sources = updated
            };
        }
    }

    public void SetSourceWriteMode(string sourceId, string writeMode)
    {
        lock (status_lock_)
        {
            DaSourceStatusSnapshot[] updated = status_.Sources
                .Select(source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
                    ? source with { WriteMode = writeMode }
                    : source)
                .ToArray();

            status_ = status_ with
            {
                DaConnectionState = AggregateConnectionState(updated),
                Sources = updated
            };
        }
    }

    public void UpdateDaRead(string sourceId, IReadOnlyList<BridgeValue> values, TimeSpan readDuration)
    {
        DateTime readTime = DateTime.UtcNow;
        double? clockOffsetMs = null;

        for (int i = 0; i < values.Count; i++)
        {
            BridgeValue value = values[i];
            values_by_key_[NormalizeKey(value.SourceId, value.ItemId)] = new BridgeValueSnapshot(
                value.SourceId,
                value.ItemId,
                value.Value,
                value.TimestampUtc,
                value.DaQuality,
                value.IsGood,
                readTime);

            // Compute clock offset from the first good value: bridge time − DA server time
            if (clockOffsetMs is null && value.IsGood && value.TimestampUtc > DateTime.MinValue)
            {
                clockOffsetMs = Math.Round((readTime - value.TimestampUtc).TotalMilliseconds, 1);
            }
        }

        // Read stats belong to value-bearing updates only: subscription callbacks pass
        // TimeSpan.Zero (there is no device read), and in subscription mode the poll cycle
        // returns only unbound items — an empty pass must not reset the count, blank the
        // duration, or drop the clock offset a callback batch just measured. The bridge
        // and connection state still advance on every pass.
        bool hasValues = values.Count > 0;
        if (hasValues)
        {
            RecordSourceValues(values.Count);
        }

        lock (status_lock_)
        {
            DaSourceStatusSnapshot[] updated = status_.Sources
                .Select(source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
                    ? source with
                    {
                        ConnectionState = "Connected",
                        LastDaReadUtc = hasValues ? readTime : source.LastDaReadUtc,
                        LastDaReadCount = hasValues ? values.Count : source.LastDaReadCount,
                        LastDaReadDurationMs = hasValues && readDuration > TimeSpan.Zero
                            ? ToMilliseconds(readDuration)
                            : source.LastDaReadDurationMs,
                        LastError = null,
                        DaClockOffsetMs = clockOffsetMs ?? source.DaClockOffsetMs
                    }
                    : source)
                .ToArray();

            bool anyFaulted = updated.Any(s => string.Equals(s.ConnectionState, "Faulted", StringComparison.OrdinalIgnoreCase));
            status_ = status_ with
            {
                BridgeState = "Running",
                DaConnectionState = AggregateConnectionState(updated),
                LastDaReadUtc = hasValues ? readTime : status_.LastDaReadUtc,
                LastDaReadCount = hasValues ? values.Count : status_.LastDaReadCount,
                LastError = anyFaulted ? status_.LastError : null,
                Sources = updated
            };
        }
    }

    public void MarkUaWrite(int valueCount, TimeSpan pollDuration)
    {
        lock (status_lock_)
        {
            double durationMs = ToMilliseconds(pollDuration);
            status_ = status_ with
            {
                // The duration describes the cycle that just ran (valid for an empty
                // subscription pass); the write counters describe the last cycle that
                // actually carried values, so quiet cycles no longer flicker 0 values
                // and a 0/s rate.
                LastPollDurationMs = durationMs,
                LastUaWriteUtc = valueCount > 0 ? DateTime.UtcNow : status_.LastUaWriteUtc,
                LastUaWriteCount = valueCount > 0 ? valueCount : status_.LastUaWriteCount,
                LastPollValueRate = valueCount > 0 ? CalculateValueRate(valueCount, pollDuration) : status_.LastPollValueRate
            };
        }
    }

    /// <summary>
    /// Records source-read values for the measured flow counters. The window resets on the
    /// first value after roughly a second, and <see cref="GetSourceValueFlow"/> divides by
    /// the window's own elapsed time, so the rate decays when the flow stops.
    /// </summary>
    private void RecordSourceValues(int count)
    {
        Interlocked.Add(ref source_values_total_, count);

        long nowTicks = DateTime.UtcNow.Ticks;
        if (nowTicks - Interlocked.Read(ref value_window_start_ticks_) >= TimeSpan.TicksPerSecond)
        {
            Interlocked.Exchange(ref value_window_start_ticks_, nowTicks);
            Interlocked.Exchange(ref value_window_count_, 0);
        }

        Interlocked.Add(ref value_window_count_, count);
    }

    /// <summary>
    /// Measured source-value flow: the monotonic total and the current window's values per
    /// second (window count over its own elapsed time, floored at 1 s).
    /// </summary>
    public (long Total, double PerSecond) GetSourceValueFlow()
    {
        long total = Interlocked.Read(ref source_values_total_);
        long count = Interlocked.Read(ref value_window_count_);
        long startTicks = Interlocked.Read(ref value_window_start_ticks_);
        double elapsedSeconds = (DateTime.UtcNow - new DateTime(startTicks, DateTimeKind.Utc)).TotalSeconds;
        if (elapsedSeconds < 1.0)
        {
            elapsedSeconds = 1.0;
        }

        return (total, Math.Round(count / elapsedSeconds, 1));
    }

    public void UpdateRateGroup(string sourceId, int rateMs, int tagCount, int tagLimit, TimeSpan readDuration)
    {
        string key = $"{sourceId}:{rateMs}";
        double durationMs = ToMilliseconds(readDuration);
        double budgetPct = rateMs > 0 ? Math.Min(100, durationMs / rateMs * 100) : 0;

        string status = "ok";
        if (tagLimit > 0 && tagCount > tagLimit) status = "limit-exceeded";
        else if (budgetPct >= 80) status = "saturated";
        else if (budgetPct >= 50) status = "warning";

        lock (status_lock_)
        {
            rate_groups_[key] = new RateGroupStatus(sourceId, rateMs, tagCount, tagLimit, durationMs, budgetPct, status);
            status_ = status_ with { RateGroups = rate_groups_.Values.OrderBy(g => g.SourceId).ThenBy(g => g.RateMs).ToArray() };
        }
    }

    public void ClearRateGroups()
    {
        lock (status_lock_)
        {
            rate_groups_.Clear();
            status_ = status_ with { RateGroups = Array.Empty<RateGroupStatus>() };
        }
    }
    public void UpdateResources(ResourceSnapshot resources)
    {
        lock (status_lock_)
        {
            status_ = status_ with { Resources = resources };
        }
    }


    public IReadOnlyList<BridgeValueSnapshot> GetValues()
    {
        return values_by_key_.Values
            .OrderBy(value => value.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public int GetValueCount() => values_by_key_.Count;

    /// <summary>
    /// All values whose last quality is bad (IsGood false), as (source, item) pairs. Used by
    /// the dashboard for the per-tag "Bad" badge — the value sample is capped, but the bad
    /// set is always complete because it is scanned from the full store.
    /// </summary>
    public IReadOnlyList<(string SourceId, string ItemId)> GetBadQualityTags()
    {
        List<(string, string)> result = new();
        foreach (KeyValuePair<string, BridgeValueSnapshot> pair in values_by_key_)
        {
            if (!pair.Value.IsGood)
            {
                result.Add((pair.Value.SourceId, pair.Value.ItemId));
            }
        }

        return result;
    }

    /// <summary>
    /// Records one interlink forwarding attempt for the consumer tag (source::item).
    /// Attempts counts immediately; the write outcome updates successes/failures and
    /// the last-error when it completes. Keys are case-insensitive and trimmed.
    /// </summary>
    public void RecordLinkForward(string consumerSourceId, string consumerItemId, bool success, string? error)
    {
        DateTime nowUtc = DateTime.UtcNow;
        string key = NormalizeKey(consumerSourceId, consumerItemId);

        link_stats_.AddOrUpdate(
            key,
            _ => new InterlinkStats(
                Attempts: 1,
                Successes: success ? 1 : 0,
                Failures: success ? 0 : 1,
                LastForwardUtc: nowUtc,
                LastWriteSuccess: success,
                LastError: error),
            (_, existing) => new InterlinkStats(
                Attempts: existing.Attempts + 1,
                Successes: existing.Successes + (success ? 1 : 0),
                Failures: existing.Failures + (success ? 0 : 1),
                LastForwardUtc: nowUtc,
                LastWriteSuccess: success,
                LastError: error));
    }

    public IReadOnlyDictionary<string, InterlinkStats> GetLinkStats()
    {
        Dictionary<string, InterlinkStats> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, InterlinkStats> pair in link_stats_)
        {
            snapshot[pair.Key] = pair.Value;
        }

        return snapshot;
    }

    /// <summary>Latest value snapshot for one tag, if any value has arrived for it.</summary>
    public bool TryGetSnapshot(string sourceId, string itemId, out BridgeValueSnapshot snapshot)
    {
        return values_by_key_.TryGetValue(NormalizeKey(sourceId, itemId), out snapshot!);
    }

    /// <summary>Value count for a specific source, or the global total when sourceId is blank.</summary>
    public int GetValueCount(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return values_by_key_.Count;
        }

        return values_by_key_.Values.Count(value =>
            string.Equals(value.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sorted but capped — for UI feeds (dashboard) where the full list is megabytes
    /// and would freeze the browser when re-rendered every poll cycle. When no source
    /// filter is given, rows are interleaved round-robin across sources so every source
    /// stays visible even when one source alone exceeds the cap.
    /// </summary>
    public IReadOnlyList<BridgeValueSnapshot> GetValues(int limit, string? sourceId = null)
    {
        if (limit <= 0)
        {
            return GetValues();
        }

        IOrderedEnumerable<BridgeValueSnapshot> ordered = values_by_key_.Values
            .OrderBy(value => value.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.ItemId, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            return ordered
                .Where(value => string.Equals(value.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToArray();
        }

        BridgeValueSnapshot[][] bySource = values_by_key_.Values
            .GroupBy(value => value.SourceId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(value => value.ItemId, StringComparer.OrdinalIgnoreCase)
                .ToArray())
            .ToArray();

        int capacity = Math.Min(limit, values_by_key_.Count);
        List<BridgeValueSnapshot> result = new(capacity);
        int[] cursors = new int[bySource.Length];
        int total = 0;
        while (total < capacity)
        {
            bool progressed = false;
            for (int s = 0; s < bySource.Length && total < capacity; s++)
            {
                if (cursors[s] < bySource[s].Length)
                {
                    result.Add(bySource[s][cursors[s]++]);
                    total++;
                    progressed = true;
                }
            }

            if (!progressed)
            {
                break;
            }
        }

        return result;
    }

    public BridgeRuntimeStatus GetStatus()
    {
        lock (status_lock_)
        {
            return status_;
        }
    }

    private static DaSourceStatusSnapshot BuildDisconnectedSnapshot(DaSourceRuntimeSettings source) =>
        new(
            source.SourceId,
            source.DisplayName,
            source.Host,
            source.ProgId,
            source.UpdateRateMs,
            "Disconnected",
            null,
            null,
            0,
            0,
            null,
            source.SourceType,
            BuildEndpointSummary(source));

    private static string BuildEndpointSummary(DaSourceRuntimeSettings source)
    {
        if (string.Equals(source.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.SourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
        {
            string port = source.SerialPortName ?? string.Empty;
            return string.IsNullOrEmpty(port) ? string.Empty : $"{port}@{source.BaudRate}";
        }

        if (string.Equals(source.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
        {
            return source.EndpointUrl?.Trim() ?? string.Empty;
        }

        string host = string.IsNullOrWhiteSpace(source.Host) ? string.Empty : source.Host.Trim();
        string progId = source.ProgId ?? string.Empty;
        return string.IsNullOrEmpty(progId) ? host : $"{host}/{progId}";
    }

    private static string AggregateConnectionState(IReadOnlyList<DaSourceStatusSnapshot> sources)
    {
        if (sources.Count == 0)
        {
            return "Disconnected";
        }

        bool anyConnected = false;
        bool anyConnecting = false;
        bool anyFaulted = false;

        for (int i = 0; i < sources.Count; i++)
        {
            string state = sources[i].ConnectionState;
            if (string.Equals(state, "Connected", StringComparison.OrdinalIgnoreCase))
            {
                anyConnected = true;
                continue;
            }

            if (string.Equals(state, "Connecting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "Reconnecting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "Switching", StringComparison.OrdinalIgnoreCase))
            {
                anyConnecting = true;
                continue;
            }

            if (string.Equals(state, "Faulted", StringComparison.OrdinalIgnoreCase))
            {
                anyFaulted = true;
            }
        }

        if (anyConnected && (anyConnecting || anyFaulted)) return "Partial";
        if (anyConnected) return "Connected";
        if (anyConnecting) return "Connecting";
        if (anyFaulted) return "Faulted";
        return "Disconnected";
    }

    private static double ToMilliseconds(TimeSpan duration)
    {
        return Math.Round(duration.TotalMilliseconds, 1);
    }

    private static double CalculateValueRate(int valueCount, TimeSpan duration)
    {
        return duration.TotalSeconds <= 0 ? 0 : Math.Round(valueCount / duration.TotalSeconds, 1);
    }

    internal static string NormalizeKey(string sourceId, string itemId)
    {
        return string.Concat(sourceId.Trim(), "::", itemId.Trim());
    }
}

public sealed record BridgeRuntimeStatus(
    string BridgeState,
    string DaConnectionState,
    int UpdateRateMs,
    int MappingCount,
    DateTime? LastDaReadUtc,
    int LastDaReadCount,
    DateTime? LastUaWriteUtc,
    int LastUaWriteCount,
    double LastPollDurationMs,
    double LastPollValueRate,
    string? LastError,
    IReadOnlyList<DaSourceStatusSnapshot> Sources,
    IReadOnlyList<RateGroupStatus> RateGroups,
    ResourceSnapshot? Resources,
    int SessionId = 0,
    bool InteractiveSession = true)
{
    public static BridgeRuntimeStatus Empty { get; } = new(
        "Stopped",
        "Disconnected",
        0,
        0,
        null,
        0,
        null,
        0,
        0,
        0,
        null,
        Array.Empty<DaSourceStatusSnapshot>(),
        Array.Empty<RateGroupStatus>(),
        null);
}

/// <summary>
/// Tags of one source polled at one update rate (a rate bucket). Applies to every
/// driver; it corresponds to an OPC DA COM group only for OpcDa sources — the
/// native serial/UA clients batch internally and never create COM groups.
/// </summary>
public sealed record RateGroupStatus(
    string SourceId,
    int RateMs,
    int TagCount,
    int TagLimit,
    double LastReadDurationMs,
    double CycleBudgetPct,
    string Status);

public sealed record DaSourceStatusSnapshot(
    string SourceId,
    string DisplayName,
    string Host,
    string ProgId,
    int UpdateRateMs,
    string ConnectionState,
    DateTime? LastDaReadUtc,
    string? LastError,
    int LastDaReadCount,
    double LastDaReadDurationMs,
    double? DaClockOffsetMs,
    string SourceType = "OpcDa",
    string EndpointSummary = "",
    string ServerInfo = "",
    string ReadMode = "",
    string WriteMode = "");

public sealed record BridgeValueSnapshot(
    string SourceId,
    string ItemId,
    object? Value,
    DateTime TimestampUtc,
    int DaQuality,
    bool IsGood,
    DateTime ServerTimestampUtc);