namespace OpcBridge.App;

/// <summary>
/// Last-resort reporting for exceptions that escape every managed boundary — the ones that
/// terminate the process instead of being caught by a worker.
///
/// This is the gap the 2026-09-29 stops fell through. Both were Service Control Manager event
/// 7034 ("terminated unexpectedly"); the morning one left a Windows Error Reporting record whose
/// only usable field was exception code <c>e0434352</c> — "the CLR raised an unhandled managed
/// exception" — with no type, no message and no stack, and the afternoon one left nothing at all.
/// The bridge's own logs could not help: they lived in the process that died.
///
/// The report goes to its own file, <c>logs/crash-&lt;stamp&gt;.log</c>, rather than into the rolling
/// <c>bridge.log</c>: a crash is the one event that must never be rotated away, and the crash path
/// deliberately shares no locks with the logging pipeline — it may be running on a thread that
/// died holding one.
/// </summary>
internal static class CrashLog
{
    private static readonly object Sync = new();

    /// <summary>
    /// Registers the handlers. Called as the first statement of <c>Program</c>, so it covers
    /// startup failures (port binding, config load, the OPC UA stack) as well as run-time ones.
    /// </summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Report("AppDomain.UnhandledException", args.ExceptionObject as Exception, args.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // A faulted task nobody awaited. Report it and mark it observed: unobserved task
            // exceptions are usually survivable, and letting this one tear the bridge down would
            // turn a lost background operation into a plant-wide outage.
            Report("TaskScheduler.UnobservedTaskException", args.Exception, isTerminating: false);
            args.SetObserved();
        };
    }

    public static void Report(string source, Exception? exception, bool isTerminating)
    {
        try
        {
            string directory = Path.Combine(DataDirectory.Value, "logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.log");

            string text = string.Join(
                Environment.NewLine,
                $"utc          : {DateTime.UtcNow:O}",
                $"computer     : {Environment.MachineName}",
                $"process      : {Environment.ProcessId}",
                $"source       : {source}",
                $"terminating  : {isTerminating}",
                $"version      : {typeof(CrashLog).Assembly.GetName().Version}",
                $"http port    : {BridgeState.HttpPort}",
                $"session      : {BridgeState.SessionId}",
                "",
                exception?.ToString() ?? "(no exception object on the event args)",
                "");

            lock (Sync)
            {
                File.WriteAllText(path, text);
            }

            // Best effort: also drop a one-line marker in the rolling log, so a reader of
            // bridge.log learns a crash report exists without having to list the directory.
            try
            {
                new FileLogStore().Append($"[FATAL] {source}: {exception?.GetType().Name ?? "unknown"} — report at {path}");
            }
            catch
            {
            }
        }
        catch
        {
            // A crash handler that throws replaces the crash with a more confusing one.
        }
    }
}
