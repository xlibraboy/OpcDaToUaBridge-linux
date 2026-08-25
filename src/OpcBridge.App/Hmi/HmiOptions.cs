namespace OpcBridge.App.Hmi;

public sealed class HmiOptions
{
    public const int DefaultBroadcastFlushMs = 100;
    public const int MinBroadcastFlushMs = 50;
    public const int MaxBroadcastFlushMs = 1000;

    /// <summary>
    /// SignalR value-batch flush interval in milliseconds. Clamped to 50–1000; default 100.
    /// </summary>
    public int BroadcastFlushMs { get; set; } = DefaultBroadcastFlushMs;

    public int GetClampedBroadcastFlushMs() => ClampBroadcastFlushMs(BroadcastFlushMs);

    public static int ClampBroadcastFlushMs(int value)
    {
        if (value < MinBroadcastFlushMs)
        {
            return MinBroadcastFlushMs;
        }

        if (value > MaxBroadcastFlushMs)
        {
            return MaxBroadcastFlushMs;
        }

        return value;
    }
}
