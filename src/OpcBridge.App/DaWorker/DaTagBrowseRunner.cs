using System.Runtime.Versioning;
using OpcBridge.Client.Workers;
using OpcBridge.Da;

namespace OpcBridge.App;

/// <summary>
/// Runs one DA tag browse: inside the source's worker process when the source is worker-placed,
/// so the address-space call carries the same identity and session context as the source's data
/// path; from the bridge process otherwise. Browsing a worker-placed source from the bridge
/// would use the bridge account — the MSI installs the service as <c>LocalSystem</c>, which
/// vendor DCOM stacks that require a plant account refuse (the very servers workers exist for).
/// </summary>
internal static class DaTagBrowseRunner
{
    public static async Task<OpcTagBrowseResult> RunAsync(
        DaTagBrowseRequest request,
        DaRuntimeSettingsSnapshot snapshot,
        IWorkerHost workers,
        TimeSpan workerRequestTimeout,
        CancellationToken cancellationToken)
    {
        if (snapshot.GetSource(request.SourceId) is { } source
            && WorkerPlacement.WorkerKeyFor(source) is { } workerKey)
        {
            return await BrowseInWorkerAsync(
                    workers,
                    workerKey,
                    source.SourceId,
                    request.Path,
                    request.Recursive,
                    workerRequestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Only the in-process path touches DA COM; a worker-placed browse runs in the
        // worker process (Windows-only by construction) and is unaffected by this guard.
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("OPC DA browsing requires Windows.");
        }

        return await BrowseInProcessAsync(request, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<OpcTagBrowseResult> BrowseInProcessAsync(
        DaTagBrowseRequest request,
        CancellationToken cancellationToken)
        => await Task.Run(
                () => OpcTagBrowser.Browse(
                    request.ProgId,
                    request.Host,
                    request.Path ?? string.Empty,
                    request.Recursive,
                    request.RemoteUsername,
                    request.RemotePassword,
                    request.RemoteDomain),
                cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<OpcTagBrowseResult> BrowseInWorkerAsync(
        IWorkerHost workers,
        string workerKey,
        string sourceId,
        string? path,
        bool recursive,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        IWorkerChannel channel = await workers
            .EnsureChannelAsync(workerKey, cancellationToken)
            .ConfigureAwait(false);

        WorkerFrame response = await channel.RequestAsync(
                WorkerFrameTypes.Browse,
                new WorkerBrowseRequest(sourceId, path, recursive),
                requestTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        WorkerBrowseResult? result = response.PayloadAs<WorkerBrowseResult>();
        if (result is null)
        {
            throw new InvalidOperationException("The worker browse returned no result.");
        }

        if (result.Error is not null)
        {
            throw new InvalidOperationException(result.Error);
        }

        return new OpcTagBrowseResult(
            result.Branches ?? Array.Empty<string>(),
            (result.Tags ?? Array.Empty<WorkerBrowseTag>())
                .Select(tag => new OpcTagNode(tag.Name, tag.ItemId, tag.CanonicalDataType, tag.AccessRights))
                .ToList(),
            result.Warnings ?? Array.Empty<string>());
    }
}
