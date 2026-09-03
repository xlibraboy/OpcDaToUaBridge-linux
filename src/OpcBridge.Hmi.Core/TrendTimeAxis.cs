namespace OpcBridge.Hmi.Core;

/// <summary>
/// Chooses clock-aligned ticks for the trend's time (X) axis — e.g. 10-minute ticks for a
/// 1h window, 2-hour ticks for an 8h window. Pure logic so it is unit-testable.
/// </summary>
public static class TrendTimeAxis
{
    private static readonly long[] LadderTicks =
    {
        TimeSpan.TicksPerSecond,
        TimeSpan.TicksPerSecond * 5,
        TimeSpan.TicksPerSecond * 10,
        TimeSpan.TicksPerSecond * 15,
        TimeSpan.TicksPerSecond * 30,
        TimeSpan.TicksPerMinute,
        TimeSpan.TicksPerMinute * 2,
        TimeSpan.TicksPerMinute * 5,
        TimeSpan.TicksPerMinute * 10,
        TimeSpan.TicksPerMinute * 15,
        TimeSpan.TicksPerMinute * 30,
        TimeSpan.TicksPerHour,
        TimeSpan.TicksPerHour * 2,
        TimeSpan.TicksPerHour * 3,
        TimeSpan.TicksPerHour * 4,
        TimeSpan.TicksPerHour * 6,
        TimeSpan.TicksPerHour * 12,
        TimeSpan.TicksPerDay
    };

    /// <summary>
    /// Smallest ladder step that keeps the tick count at or below <paramref name="maxTicks"/>.
    /// </summary>
    public static TimeSpan StepFor(TimeSpan span, int maxTicks = 6)
    {
        if (span <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        foreach (long step in LadderTicks)
        {
            if (span.Ticks / step <= maxTicks)
            {
                return new TimeSpan(step);
            }
        }

        return new TimeSpan(LadderTicks[^1]);
    }

    /// <summary>Floors a time to the start of the containing step (e.g. 13:47 → 13:40 on 10m ticks).</summary>
    public static DateTime Floor(DateTime time, TimeSpan step)
    {
        long ticks = step.Ticks > 0 ? step.Ticks : TimeSpan.TicksPerSecond;
        return new DateTime((time.Ticks / ticks) * ticks, time.Kind);
    }
}
