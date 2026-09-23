using System.Collections.Concurrent;

namespace OpcBridge.App.Auth;

/// <summary>
/// Fixed-window failure counter for the login endpoint, keyed by "username|client-ip".
/// Small and in-memory on purpose: the bridge is a single process behind a plant LAN,
/// and the point is to blunt scripted password guessing (not to be a WAF).
/// </summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBlock = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (int Failures, DateTime WindowStartUtc)> entries_ = new(StringComparer.Ordinal);

    public static string BuildKey(string? username, string? clientIp) =>
        $"{(username ?? string.Empty).Trim().ToLowerInvariant()}|{clientIp ?? "unknown"}";

    /// <summary>True while the key is over the failure budget. RetryAfterSeconds is a floor of 1.</summary>
    public bool IsBlocked(string key, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (!entries_.TryGetValue(key, out (int Failures, DateTime WindowStartUtc) entry))
        {
            return false;
        }

        TimeSpan elapsed = DateTime.UtcNow - entry.WindowStartUtc;
        if (elapsed >= Window)
        {
            entries_.TryRemove(key, out _);
            return false;
        }

        if (entry.Failures < MaxFailures)
        {
            return false;
        }

        retryAfterSeconds = (int)Math.Max(1, Math.Min(MaxBlock.TotalSeconds, (Window - elapsed).TotalSeconds));
        return true;
    }

    public void RecordFailure(string key)
    {
        DateTime now = DateTime.UtcNow;
        entries_.AddOrUpdate(
            key,
            _ => (1, now),
            (_, existing) => (now - existing.WindowStartUtc) >= Window ? (1, now) : (existing.Failures + 1, existing.WindowStartUtc));

        // Opportunistic cleanup so a long-running bridge does not accumulate keys forever.
        if (entries_.Count > 1000)
        {
            foreach (KeyValuePair<string, (int Failures, DateTime WindowStartUtc)> entry in entries_)
            {
                if (now - entry.Value.WindowStartUtc >= Window)
                {
                    entries_.TryRemove(entry.Key, out _);
                }
            }
        }
    }

    public void Reset(string key) => entries_.TryRemove(key, out _);
}
