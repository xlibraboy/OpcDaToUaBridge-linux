using System.Collections.Concurrent;
using OpcBridge.Core;

namespace OpcBridge.App;

public sealed class BridgeState
{
    private readonly ConcurrentDictionary<string, BridgeValueSnapshot> values_by_key_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RateGroupStatus> rate_groups_ = new(StringComparer.OrdinalIgnoreCase);
    private BridgeRuntimeStatus status_ = BridgeRuntimeStatus.Empty;
    private readonly object status_lock_ = new();

    public void Configure(int updateRateMs, int mappingCount, IReadOnlyList<DaSourceRuntimeSettings> sources)
    {
        DaSourceStatusSnapshot[] sourceStatuses = sources
            .Select(source => new DaSourceStatusSnapshot(
                source.SourceId,
                source.DisplayName,
                source.Host,
                source.ProgId,
                source.UpdateRateMs,
                "Disconnected",
                null,
                null,
                0,
                0))
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
                    previous = new DaSourceStatusSnapshot(
                        source.SourceId,
                        source.DisplayName,
                        source.Host,
                        source.ProgId,
                        source.UpdateRateMs,
                        "Disconnected",
                        null,
                        null,
                        0,
                        0);
                }
                else
                {
                    previous = previous with
                    {
                        DisplayName = source.DisplayName,
                        Host = source.Host,
                        ProgId = source.ProgId,
                        UpdateRateMs = source.UpdateRateMs
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
        values_by_key_[NormalizeKey(value.SourceId, value.DaItemId)] = new BridgeValueSnapshot(
            value.SourceId,
            value.DaItemId,
            value.Value,
            value.TimestampUtc,
            value.DaQuality,
            value.IsGood);
    }

    public void ClearValue(string sourceId, string daItemId)
    {
        values_by_key_.TryRemove(NormalizeKey(sourceId, daItemId), out _);
    }

    public void RetainMappedValues(IReadOnlyList<TagMapping> mappings)
    {
        HashSet<string> mappedKeys = mappings
            .Select(mapping => NormalizeKey(mapping.SourceId, mapping.DaItemId))
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
            DaSourceStatusSnapshot[] updated = status_.Sources
                .Select(source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
                    ? source with
                    {
                        ConnectionState = "Faulted",
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

    public void UpdateDaRead(string sourceId, IReadOnlyList<BridgeValue> values, TimeSpan readDuration)
    {
        DateTime readTime = DateTime.UtcNow;

        for (int i = 0; i < values.Count; i++)
        {
            BridgeValue value = values[i];
            values_by_key_[NormalizeKey(value.SourceId, value.DaItemId)] = new BridgeValueSnapshot(
                value.SourceId,
                value.DaItemId,
                value.Value,
                value.TimestampUtc,
                value.DaQuality,
                value.IsGood);
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
                        LastError = null
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

    public IReadOnlyList<BridgeValueSnapshot> GetValues()
    {
        return values_by_key_.Values
            .OrderBy(value => value.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.DaItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public BridgeRuntimeStatus GetStatus()
    {
        lock (status_lock_)
        {
            return status_;
        }
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

    private static string NormalizeKey(string sourceId, string daItemId)
    {
        return string.Concat(sourceId.Trim(), "::", daItemId.Trim());
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
    IReadOnlyList<RateGroupStatus> RateGroups)
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
        Array.Empty<RateGroupStatus>());
}

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
    double LastDaReadDurationMs);

public sealed record BridgeValueSnapshot(
    string SourceId,
    string DaItemId,
    object? Value,
    DateTime TimestampUtc,
    int DaQuality,
    bool IsGood);