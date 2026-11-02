namespace OpcBridge.Mobile.Core;

/// <summary>
/// The compact forms of the bridge readings used beside the link strip and in the bridge rows.
/// The full words stay on the settings rows and in the accessible descriptions; the strip only
/// has room for the value, and the state colour carries the quality.
/// </summary>
public static class BridgeReadingText
{
    /// <summary>"good · 12 ms" → "12 ms"; "latency —" → "—"; "offline" stays "offline".</summary>
    public static string LatencyValue(string? text)
    {
        string value = text ?? "latency —";
        if (string.Equals(value, "latency —", StringComparison.Ordinal))
        {
            return "—";
        }

        int separator = value.LastIndexOf(" · ", StringComparison.Ordinal);
        return separator >= 0 ? value[(separator + 3)..] : value;
    }

    /// <summary>"synced · 12:31:05" → "synced 12:31:05"; every other state is unchanged.</summary>
    public static string SyncStamp(string? text)
    {
        string value = text ?? "not connected";
        const string synced = "synced · ";
        return value.StartsWith(synced, StringComparison.Ordinal)
            ? "synced " + value[synced.Length..]
            : value;
    }
}
