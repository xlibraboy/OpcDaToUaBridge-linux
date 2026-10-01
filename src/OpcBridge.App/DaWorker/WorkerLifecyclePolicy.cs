namespace OpcBridge.App;

/// <summary>
/// Pure restart/quarantine policy for worker processes. Backoff grows 1-2-5-10-30 s; a worker
/// that crashes <see cref="QuarantineThreshold"/> times inside <see cref="QuarantineWindow"/>
/// is quarantined — no automatic restart (the Workers board offers a manual one), so a
/// crash-looping vendor DLL cannot spin forever.
/// </summary>
internal static class WorkerLifecyclePolicy
{
    public const int QuarantineThreshold = 5;

    public static readonly TimeSpan QuarantineWindow = TimeSpan.FromMinutes(10);

    public static TimeSpan BackoffForAttempt(int attempt) => attempt switch
    {
        <= 0 => TimeSpan.Zero,
        1 => TimeSpan.FromSeconds(1),
        2 => TimeSpan.FromSeconds(2),
        3 => TimeSpan.FromSeconds(5),
        4 => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(30)
    };

    public static bool ShouldQuarantine(IReadOnlyList<DateTime> crashesUtc, DateTime nowUtc)
    {
        int recent = crashesUtc.Count(crash => nowUtc - crash <= QuarantineWindow);
        return recent >= QuarantineThreshold;
    }

    public static string ClassifyExit(int exitCode) => exitCode switch
    {
        DaWorkerHost.ExitClean => "clean-exit",
        DaWorkerHost.ExitPipeClosed => "parent-gone",
        DaWorkerHost.ExitBootstrapError => "bootstrap-error",
        DaWorkerHost.ExitProtocolError => "protocol-error",
        _ => "crashed"
    };
}
