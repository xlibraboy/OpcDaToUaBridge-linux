namespace OpcBridge.Hmi.Core;

/// <summary>
/// Live state of one bridge's HMI link. Every surface that says "connected" is derived from
/// this instead of being latched when the connect succeeded.
/// </summary>
public enum BridgeLinkState
{
    /// <summary>The SignalR link is up and the tag snapshot is current.</summary>
    Connected,

    /// <summary>The link dropped; the hub client is retrying and the cache may be stale.</summary>
    Reconnecting,

    /// <summary>No usable link (connect failed or the hub closed); a retry is scheduled.</summary>
    Failed
}

/// <summary>One bridge's link state and the last error behind it, for the status surfaces.</summary>
public sealed record BridgeLinkStatus(string BridgeId, BridgeLinkState State, string? Error);

/// <summary>How the bridge set as a whole reads on the status strip.</summary>
public enum BridgeLinkAggregate
{
    Disconnected,
    Connected,
    Reconnecting,
    Failed
}

/// <summary>
/// Rolls the per-bridge link states into one label plus a detail line. Pure, so the exact
/// wording and the worst-state-wins rule are pinned by tests instead of the view model.
/// </summary>
public static class BridgeLinkSummary
{
    public static (BridgeLinkAggregate State, string Label, string Detail) Build(
        IReadOnlyList<BridgeLinkStatus> statuses)
    {
        if (statuses.Count == 0)
        {
            return (BridgeLinkAggregate.Disconnected, "Disconnected", "Not connected");
        }

        int connected = statuses.Count(status => status.State == BridgeLinkState.Connected);
        string counts = connected == 1 && statuses.Count == 1
            ? "1 bridge connected"
            : $"{connected}/{statuses.Count} bridges connected";

        if (connected == statuses.Count)
        {
            return (BridgeLinkAggregate.Connected, "Connected", counts);
        }

        IEnumerable<BridgeLinkStatus> reconnecting =
            statuses.Where(status => status.State == BridgeLinkState.Reconnecting);
        if (reconnecting.Any())
        {
            string names = string.Join(", ", reconnecting.Select(status => status.BridgeId));
            return (BridgeLinkAggregate.Reconnecting, "Reconnecting", $"{counts} — reconnecting: {names}");
        }

        string failed = string.Join(
            ", ",
            statuses.Where(status => status.State == BridgeLinkState.Failed).Select(FormatFailed));
        return (BridgeLinkAggregate.Failed, "Failed", $"{counts} — failed: {failed}");
    }

    private static string FormatFailed(BridgeLinkStatus status) =>
        string.IsNullOrWhiteSpace(status.Error) ? status.BridgeId : $"{status.BridgeId} ({status.Error})";
}

/// <summary>
/// Retry helper for the post-reconnect snapshot refresh: the whole point of the resync is
/// that it must not give up after one swallowed failure, so the attempt itself is the unit
/// and the schedule lives with the caller.
/// </summary>
public static class SnapshotRefresh
{
    /// <summary>
    /// Runs <paramref name="attempt"/> once, then once more after each delay (the list holds
    /// the waits between attempts). Returns true on the first success, false when every
    /// attempt failed. Cancellation propagates.
    /// </summary>
    public static async Task<bool> TryAsync(
        Func<CancellationToken, Task> attempt,
        IReadOnlyList<TimeSpan> delays,
        CancellationToken ct)
    {
        for (int i = 0; i <= delays.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await attempt(ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // retried below until the schedule is exhausted
            }

            if (i < delays.Count && delays[i] > TimeSpan.Zero)
            {
                await Task.Delay(delays[i], ct).ConfigureAwait(false);
            }
        }

        return false;
    }
}
