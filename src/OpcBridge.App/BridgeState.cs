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

    public static void ConfigurePorts(int httpPort, int uaPort, bool httpAuto, bool uaAuto)
    {
        HttpPort = httpPort;
        UaPort = uaPort;
        HttpAutoAssigned = httpAuto;
        UaAutoAssigned = uaAuto;
    }


    public void Configure(int updateRateMs, int mappingCount, IReadOnlyList<DaSourceRuntimeSettings> sources)
    {
        DaSourceStatusSnapshot[] sourceStatuses = sources
            .Select(BuildDisconnectedSnapshot)
            .ToArray();

        rate_groups_.Clear();
        lock (status_lock_)
        {
            status_ = status_ with
            {
                BridgeState = "Starting",
                UpdateRateMs = updateRateMs,
                MappingCount = mappingCount,
                DaConnectionState = AggregateConnectionState(sourceStatuses),
                LastDaReadUtc = null,
                LastDaReadCount = 0,
                LastUaWriteUtc = null,
                LastUaWriteCount = 0,
                LastPollDurationMs = 0,
                LastPollValueRate = 0,
                LastError = null,
                Sources = sourceStatuses
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
        values_by_key_[NormalizeKey(value.SourceId, value.ItemId)] = new BridgeValueSnapshot(
            value.SourceId,
            value.ItemId,
            value.Value,
            value.TimestampUtc,
            value.DaQuality,
            value.IsGood);

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
                value.IsGood);

            // Compute clock offset from the first good value: bridge time − DA server time
            if (clockOffsetMs is null && value.IsGood && value.TimestampUtc > DateTime.MinValue)
            {
                clockOffsetMs = Math.Round((readTime - value.TimestampUtc).TotalMilliseconds, 1);
            }
        }

        lock (status_lock_)
        {
            DaSourceStatusSnapshot[] updated = status_.Sources
                .Select(source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
                    ? source with
                    {
                        ConnectionState = "Connected",
                        LastDaReadUtc = readTime,
                        LastDaReadCount = values.Count,
                        LastDaReadDurationMs = ToMilliseconds(readDuration),
                        LastError = null,
                        DaClockOffsetMs = clockOffsetMs
                    }
                    : source)
                .ToArray();

            bool anyFaulted = updated.Any(s => string.Equals(s.ConnectionState, "Faulted", StringComparison.OrdinalIgnoreCase));
            status_ = status_ with
            {
                BridgeState = "Running",
                DaConnectionState = AggregateConnectionState(updated),
                LastDaReadUtc = readTime,
                LastDaReadCount = values.Count,
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
                LastUaWriteUtc = DateTime.UtcNow,
                LastUaWriteCount = valueCount,
                LastPollDurationMs = durationMs,
                LastPollValueRate = CalculateValueRate(valueCount, pollDuration)
            };
        }
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

        if (string.Equals(source.SourceType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
        {
            return $"MX station {source.LogicalStationNumber}";
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
    ResourceSnapshot? Resources)
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
/// driver; it corresponds to an OPC DA COM group only for OpcDa sources — MX
/// Component and other native clients batch internally and never create COM groups.
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
    string ServerInfo = "");

public sealed record BridgeValueSnapshot(
    string SourceId,
    string ItemId,
    object? Value,
    DateTime TimestampUtc,
    int DaQuality,
    bool IsGood);