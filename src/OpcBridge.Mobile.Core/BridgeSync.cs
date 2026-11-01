namespace OpcBridge.Mobile.Core;

/// <summary>
/// How far one bridge's phone data has moved from the bridge: the value shown beside the
/// connection status. <see cref="Syncing"/> is a read/flush in progress, <see cref="Offline"/>
/// means the link is down, <see cref="Pending"/> means the link is up but the outbox still has
/// work, and <see cref="Error"/> means the outbox has items the bridge refused.
/// </summary>
public enum BridgeSyncState
{
    Synced,
    Syncing,
    Offline,
    Pending,
    Error
}

/// <summary>
/// The phone-to-bridge round-trip health. The latency is measured with a lightweight
/// <c>GET /health</c>, not inferred from the last refresh: it answers "how far away is the
/// bridge on this network?" rather than "how long did three API calls take?".
/// </summary>
public enum LatencyState
{
    Unknown,
    Good,
    Fair,
    Poor,
    Offline
}

/// <summary>Thresholds and wording for the latency badge.</summary>
public static class LatencyGrade
{
    /// <summary>At or under this many milliseconds the link reads GOOD.</summary>
    public const int GoodMaxMs = 50;

    /// <summary>At or under this many milliseconds the link reads FAIR; above it, POOR.</summary>
    public const int FairMaxMs = 150;

    public static LatencyState Classify(int milliseconds) =>
        milliseconds <= GoodMaxMs
            ? LatencyState.Good
            : milliseconds <= FairMaxMs
                ? LatencyState.Fair
                : LatencyState.Poor;

    public static string Key(LatencyState state) => state switch
    {
        LatencyState.Good => "good",
        LatencyState.Fair => "fair",
        LatencyState.Poor => "poor",
        LatencyState.Offline => "offline",
        _ => "unknown"
    };

    public static string Text(LatencyState state, int? milliseconds) => state switch
    {
        LatencyState.Good => "good · " + milliseconds + " ms",
        LatencyState.Fair => "fair · " + milliseconds + " ms",
        LatencyState.Poor => "poor · " + milliseconds + " ms",
        LatencyState.Offline => "offline",
        _ => "latency —"
    };
}
