using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpcBridge.Client.Workers;
using OpcBridge.Da;

namespace OpcBridge.App;

/// <summary>Snapshot of one worker for the Workers board and diagnostics.</summary>
internal sealed record DaWorkerStatus(
    string WorkerId,
    string Account,
    int Pid,
    bool Running,
    bool Quarantined,
    bool OperatorStopped,
    int ClientCount,
    IReadOnlyList<string> Sources,
    long WorkingSetBytes,
    long PrivateBytes,
    int Handles,
    DateTime? StartedUtc,
    long HeartbeatAgeMs,
    int RestartCount,
    int? LastExitCode,
    DateTime? LastExitUtc,
    string? LastError);

/// <summary>One worker lifecycle event for the Workers board's crash timeline.</summary>
internal sealed record DaWorkerEvent(DateTime Utc, string WorkerId, string Event, int? ExitCode, string? Message);

/// <summary>
/// Owns the worker processes: spawns one per placement key on demand, tracks crashes and
/// quarantine, forwards worker stderr into the bridge log, samples memory, and tears a worker
/// down when its last source disconnects. Proxies ask it for a channel on every (re)connect,
/// so BridgeWorker's existing retry loop drives the restarts.
/// </summary>
internal sealed class DaWorkerSupervisor : BackgroundService, IWorkerHost
{
    private const int HistoryLimit = 50;

    private readonly DaRuntimeSettings settings_;
    private readonly ILogger<DaWorkerSupervisor> logger_;
    private readonly ConcurrentDictionary<string, Entry> workers_ = new(StringComparer.Ordinal);
    private readonly Queue<DaWorkerEvent> history_ = new();

    private sealed class Entry
    {
        public int ClientCount;
        public DaWorkerProcess? Process;
        public readonly List<DateTime> CrashesUtc = new();
        public DateTime? LastExitUtc;
        public int? LastExitCode;
        public string? LastError;
        public bool Quarantined;
        // Operator Kill is sticky: the worker stays down until Restart on the Workers board
        // or a settings change (which bumps the version and releases the stop).
        public bool OperatorStopped;
        public long OperatorStoppedVersion;
        public DateTime LastHeartbeatUtc = DateTime.UtcNow;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    // How long a worker may stay up after its last client released it, so a session rebuild
    // (dispose + recreate in the same pass) can re-attach instead of racing a teardown.
    private static readonly TimeSpan ReapGrace = TimeSpan.FromSeconds(2);

    public DaWorkerSupervisor(DaRuntimeSettings settings, ILogger<DaWorkerSupervisor> logger)
    {
        settings_ = settings;
        logger_ = logger;
    }

    /// <summary>Creates the proxy for one source and counts it against the worker.</summary>
    public WorkerSourceClient CreateClient(string workerKey, DaSourceRuntimeSettings source)
    {
        Entry entry = workers_.GetOrAdd(workerKey, _ => new Entry());
        Interlocked.Increment(ref entry.ClientCount);
        return new WorkerSourceClient(this, workerKey, source.SourceId);
    }

    public async Task<IWorkerChannel> EnsureChannelAsync(string workerKey, CancellationToken cancellationToken)
    {
        Entry entry = workers_.GetOrAdd(workerKey, _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DaWorkerProcess? process = entry.Process;
            if (process is not null && !process.HasExited && process.Connection.ClosedReason is null)
            {
                return process.Connection;
            }

            if (process is not null)
            {
                RecordExit(entry, process);
                entry.Process = null;
            }

            if (entry.Quarantined)
            {
                throw new InvalidOperationException(
                    $"Worker '{workerKey}' is quarantined after repeated crashes " +
                    $"({entry.LastError ?? "unknown error"}). Restart it from Ops ▸ Workers.");
            }

            DaRuntimeSettingsSnapshot snapshot = settings_.GetSnapshot();

            if (entry.OperatorStopped)
            {
                if (snapshot.Version == entry.OperatorStoppedVersion)
                {
                    // Transient so the source keeps retrying with backoff: pressing Restart
                    // (or changing the source's worker settings) lets the very next pass in.
                    throw new SourceConnectionLostException(
                        $"Worker '{workerKey}' was stopped by the operator. " +
                        "Press Restart on Ops ▸ Workers to run it again.");
                }

                entry.OperatorStopped = false;
            }

            TimeSpan backoff = WorkerLifecyclePolicy.BackoffForAttempt(entry.CrashesUtc.Count);
            if (backoff > TimeSpan.Zero)
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }

            List<DaSourceRuntimeSettings> placed = WorkerPlacement.SourcesFor(snapshot, workerKey).ToList();
            if (placed.Count == 0)
            {
                throw new InvalidOperationException($"No sources are placed on worker '{workerKey}'.");
            }

            List<WorkerSourceConfig> configs = placed
                .Select(source => ToSourceConfig(source, snapshot.UseSubscriptions))
                .ToList();

            DaWorkerProcess started = await DaWorkerProcess
                .StartAsync(workerKey, placed[0].Worker, configs, cancellationToken)
                .ConfigureAwait(false);

            started.LogLine += line => logger_.LogInformation(
                "[worker {WorkerKey} pid {Pid}] {Line}",
                workerKey,
                started.Pid,
                line);
            started.Connection.PushReceived += frame =>
            {
                if (frame.Type == WorkerFrameTypes.Heartbeat)
                {
                    entry.LastHeartbeatUtc = DateTime.UtcNow;
                }
            };
            started.Connection.Closed += reason =>
            {
                logger_.LogWarning(
                    "Worker {WorkerKey} (pid {Pid}) disconnected: {Reason}",
                    workerKey,
                    started.Pid,
                    reason?.Message ?? "pipe closed");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await started.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    RecordExit(entry, started);
                });
            };

            entry.Process = started;
            AddHistory(workerKey, "started", null, $"pid {started.Pid}, {configs.Count} source(s)");
            logger_.LogInformation(
                "Started DA worker {WorkerKey} (pid {Pid}, {Count} source(s))",
                workerKey,
                started.Pid,
                configs.Count);
            return started.Connection;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public void ReleaseClient(string workerKey)
    {
        if (!workers_.TryGetValue(workerKey, out Entry? entry))
        {
            return;
        }

        if (Interlocked.Decrement(ref entry.ClientCount) > 0)
        {
            return;
        }

        _ = Task.Run(() => ReapWorkerAsync(workerKey, entry, entry.Process));
    }

    /// <summary>
    /// Stops a worker whose last client just left — unless a session rebuild re-attached in
    /// the meantime. The rebuild path disposes the old proxy and creates the new one in the
    /// same pass, so an unguarded teardown raced ahead and killed the fresh worker (and once
    /// removed the entry while its process was still serving values: no board row, dead Kill).
    /// </summary>
    private async Task ReapWorkerAsync(string workerKey, Entry entry, DaWorkerProcess? released)
    {
        try
        {
            await Task.Delay(ReapGrace).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref entry.ClientCount) > 0
                || !ReferenceEquals(entry.Process, released))
            {
                return;
            }

            DaWorkerProcess? process = entry.Process;
            if (process is null)
            {
                // Nothing to stop. Keep entries that carry operator or crash state — the
                // board row, the stop flag and the crash timeline must outlive the process,
                // unless the worker no longer has any sources placed on it at all.
                bool placementGone = WorkerPlacement.SourcesFor(settings_.GetSnapshot(), workerKey).Count == 0;
                if ((entry.OperatorStopped || entry.Quarantined) && !placementGone)
                {
                    return;
                }
            }
            else
            {
                entry.Process = null;
                process.MarkStopping();
                await process.ShutdownAsync().ConfigureAwait(false);
                RecordExit(entry, process);
                await process.DisposeAsync().ConfigureAwait(false);
            }

            // Never remove an entry a caller is holding on to: only this exact instance goes.
            ((ICollection<KeyValuePair<string, Entry>>)workers_).Remove(
                new KeyValuePair<string, Entry>(workerKey, entry));
        }
        catch (Exception ex)
        {
            logger_.LogWarning(ex, "Reaping worker {WorkerKey} failed", workerKey);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public IReadOnlyList<DaWorkerStatus> GetStatus()
    {
        DaRuntimeSettingsSnapshot snapshot = settings_.GetSnapshot();
        return workers_.Select(pair =>
        {
            Entry entry = pair.Value;
            DaWorkerProcess? process = entry.Process;
            List<DaSourceRuntimeSettings> placed = WorkerPlacement.SourcesFor(snapshot, pair.Key).ToList();
            bool running = process is not null && !process.HasExited;
            return new DaWorkerStatus(
                pair.Key,
                placed.FirstOrDefault()?.Worker.RunAsUser ?? "(bridge identity)",
                running ? process!.Pid : 0,
                running,
                entry.Quarantined,
                entry.OperatorStopped,
                entry.ClientCount,
                placed.Select(source => source.SourceId).ToList(),
                process?.WorkingSetBytes ?? 0,
                process?.PrivateBytes ?? 0,
                process?.HandleCount ?? 0,
                process?.StartedUtc,
                running ? (long)(DateTime.UtcNow - entry.LastHeartbeatUtc).TotalMilliseconds : -1,
                entry.CrashesUtc.Count,
                entry.LastExitCode,
                entry.LastExitUtc,
                entry.LastError);
        }).ToList();
    }

    /// <summary>
    /// Operator Kill: stops the worker and keeps it down. Without the stop flag the source's
    /// retry loop respawned a worker within a second, so "kill" looked like it never happened.
    /// </summary>
    public async Task<bool> KillAsync(string workerKey)
    {
        if (!workers_.TryGetValue(workerKey, out Entry? entry))
        {
            return false;
        }

        AddHistory(workerKey, "killed", null, "operator request");
        entry.OperatorStopped = true;
        entry.OperatorStoppedVersion = settings_.GetSnapshot().Version;
        await StopWorkerAsync(workerKey, entry, removeEntry: false).ConfigureAwait(false);
        return true;
    }

    /// <summary>Clears quarantine and the operator stop, then stops the worker so the next
    /// connect attempt spawns a fresh one.</summary>
    public async Task<bool> RestartAsync(string workerKey)
    {
        if (!workers_.TryGetValue(workerKey, out Entry? entry))
        {
            return false;
        }

        AddHistory(workerKey, "restart-requested", null, "operator request");
        entry.Quarantined = false;
        entry.OperatorStopped = false;
        entry.CrashesUtc.Clear();
        await StopWorkerAsync(workerKey, entry, removeEntry: false).ConfigureAwait(false);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var pair in workers_.ToArray())
        {
            await StopWorkerAsync(pair.Key, pair.Value, removeEntry: true).ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StopWorkerAsync(string workerKey, Entry entry, bool removeEntry)
    {
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DaWorkerProcess? process = entry.Process;
            entry.Process = null;
            if (process is not null)
            {
                process.MarkStopping();
                await process.ShutdownAsync().ConfigureAwait(false);
                RecordExit(entry, process);
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger_.LogWarning(ex, "Stopping worker {WorkerKey} failed", workerKey);
        }
        finally
        {
            entry.Gate.Release();
        }

        if (removeEntry)
        {
            workers_.TryRemove(workerKey, out _);
        }
    }

    private void RecordExit(Entry entry, DaWorkerProcess process)
    {
        if (process.ExitRecorded)
        {
            return;
        }

        process.ExitRecorded = true;
        DateTime now = DateTime.UtcNow;
        entry.LastExitUtc = now;
        entry.LastExitCode = process.HasExited ? process.ExitCode : 0;
        entry.LastError = process.Connection.ClosedReason?.Message;

        if (process.Stopping)
        {
            AddHistory(process.WorkerId, "stopped", entry.LastExitCode, entry.LastError);
            return;
        }

        AddHistory(
            process.WorkerId,
            "crashed",
            entry.LastExitCode,
            entry.LastError ?? WorkerLifecyclePolicy.ClassifyExit(entry.LastExitCode ?? 0));

        entry.CrashesUtc.Add(now);
        entry.CrashesUtc.RemoveAll(crash => now - crash > WorkerLifecyclePolicy.QuarantineWindow);
        if (WorkerLifecyclePolicy.ShouldQuarantine(entry.CrashesUtc, now))
        {
            entry.Quarantined = true;
            AddHistory(process.WorkerId, "quarantined", null, $"{entry.CrashesUtc.Count} crashes within {WorkerLifecyclePolicy.QuarantineWindow}");
            logger_.LogError(
                "Worker {WorkerKey} quarantined after {Count} crashes within {Window}",
                process.WorkerId,
                entry.CrashesUtc.Count,
                WorkerLifecyclePolicy.QuarantineWindow);
        }
    }

    /// <summary>Newest-last lifecycle events, capped at <see cref="HistoryLimit"/>.</summary>
    public IReadOnlyList<DaWorkerEvent> GetHistory()
    {
        lock (history_)
        {
            return history_.ToArray();
        }
    }

    private void AddHistory(string workerId, string @event, int? exitCode, string? message)
    {
        lock (history_)
        {
            history_.Enqueue(new DaWorkerEvent(DateTime.UtcNow, workerId, @event, exitCode, message));
            while (history_.Count > HistoryLimit)
            {
                history_.Dequeue();
            }
        }
    }

    private static WorkerSourceConfig ToSourceConfig(DaSourceRuntimeSettings source, bool globalUseSubscriptions) => new(
        source.SourceId,
        source.ProgId,
        source.Host,
        source.RemoteUsername,
        source.RemotePassword,
        source.RemoteDomain,
        SourceConfigMigration.NormalizeIoMode(source.IoMode),
        source.UpdateRateMs,
        globalUseSubscriptions && source.UseSubscriptions,
        source.GroupIoModes.Select(group => new WorkerGroupIoMode(group.Name, group.Rate, group.IoMode)).ToList(),
        source.WatchdogTimeoutMs);
}
