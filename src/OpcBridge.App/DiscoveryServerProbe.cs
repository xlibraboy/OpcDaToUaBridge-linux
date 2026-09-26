using System.Diagnostics;

namespace OpcBridge.App;

/// <summary>
/// Detects the OPC UA Local Discovery Server — the OPC Foundation's <c>opcualds</c>, shipped
/// with Siemens SIMATIC WinCC, Matrikon and Kepware installs. It takes port 4840 by
/// convention for discovery, so a bridge sharing that port leaves a client on the same
/// machine that resolves <c>localhost</c> to <c>::1</c> talking to the LDS instead of the
/// bridge's own address space.
/// </summary>
internal sealed class DiscoveryServerProbe
{
    private static readonly string[] KnownProcessNames = { "opcualds" };

    private static readonly TimeSpan DefaultCacheWindow = TimeSpan.FromSeconds(30);

    private readonly TimeProvider clock_;
    private readonly Func<string, bool> is_running_;
    private readonly TimeSpan cache_window_;
    private readonly object gate_ = new();

    private string? cached_name_;
    private DateTimeOffset cached_utc_ = DateTimeOffset.MinValue;

    public DiscoveryServerProbe()
        : this(TimeProvider.System, ProcessIsRunning, DefaultCacheWindow)
    {
    }

    /// <summary>Test seam: the cache window is measured in seconds, so a test moves the clock.</summary>
    internal DiscoveryServerProbe(TimeProvider clock, Func<string, bool> isRunning, TimeSpan? cacheWindow = null)
    {
        clock_ = clock;
        is_running_ = isRunning;
        cache_window_ = cacheWindow ?? DefaultCacheWindow;
    }

    /// <summary>
    /// Name of a running discovery server, or null when none is installed/running. The result
    /// is cached for the cache window because <c>/api/status/ports</c> is polled every second
    /// and the scan walks the process list.
    /// </summary>
    public string? Detect()
    {
        lock (gate_)
        {
            DateTimeOffset now = clock_.GetUtcNow();
            if (now - cached_utc_ < cache_window_)
            {
                return cached_name_;
            }

            string? detected = null;
            foreach (string name in KnownProcessNames)
            {
                if (is_running_(name))
                {
                    detected = name;
                    break;
                }
            }

            cached_name_ = detected;
            cached_utc_ = now;
            return detected;
        }
    }

    private static bool ProcessIsRunning(string name)
    {
        try
        {
            // Disposed as soon as it is queried: an undisposed Process object holds a handle
            // open until the finalizer runs.
            using Process? process = Process.GetProcessesByName(name).FirstOrDefault();
            return process is not null;
        }
        catch
        {
            // Host inspection is best-effort: a failure must not break the status endpoint.
            return false;
        }
    }
}
