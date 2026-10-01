using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Pure placement of sources onto worker processes: which sources run in-process and which
/// share a worker. The key is <c>own:{sourceId}</c> for a dedicated worker or
/// <c>acct:{account}</c> for the shared worker of a run-as account (group mode).
/// </summary>
internal static class WorkerPlacement
{
    public const string OwnPrefix = "own:";
    public const string AccountPrefix = "acct:";

    /// <summary>Worker key for the source, or null when the source runs in the bridge process.</summary>
    public static string? WorkerKeyFor(DaSourceRuntimeSettings source)
    {
        if (!string.Equals(source.SourceType, SourceTypes.OpcDa, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        DaWorkerOptions worker = source.Worker;
        string mode = DaWorkerModes.Normalize(worker.Mode);
        if (mode == DaWorkerModes.InProcess)
        {
            return null;
        }

        if (mode == DaWorkerModes.Own)
        {
            return OwnPrefix + source.SourceId;
        }

        // Group: every source with the same account shares one worker. Normalization
        // downgrades a group without an account to in-process, so the account is present.
        return string.IsNullOrWhiteSpace(worker.RunAsUser)
            ? null
            : AccountPrefix + NormalizeAccount(worker.RunAsUser!);
    }

    /// <summary>Sources that belong to the worker with this key in the given snapshot.</summary>
    public static IReadOnlyList<DaSourceRuntimeSettings> SourcesFor(
        DaRuntimeSettingsSnapshot snapshot,
        string workerKey)
        => snapshot.Sources
            .Where(source => string.Equals(WorkerKeyFor(source), workerKey, StringComparison.Ordinal))
            .ToList();

    /// <summary>Canonical account identity for grouping (case-insensitive, trimmed).</summary>
    public static string NormalizeAccount(string account) => account.Trim().ToUpperInvariant();
}
