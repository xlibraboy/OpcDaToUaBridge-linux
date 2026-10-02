using OpcBridge.Da;

namespace OpcBridge.App;

/// <summary>
/// Routes OPC DA sources to a worker process when they opt in (<see cref="WorkerPlacement"/>)
/// and to the in-process clients otherwise. BridgeWorker keeps calling the same
/// <see cref="ISourceClient"/> seam, so its retry, watchdog and value pipeline are unchanged.
/// </summary>
internal sealed class RoutingSourceClientFactory : SourceClientFactory
{
    private readonly IWorkerHost? workerHost_;

    public RoutingSourceClientFactory(IWorkerHost? workerHost = null, Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : base(loggerFactory)
    {
        workerHost_ = workerHost;
    }

    public override ISourceClient Create(DaRuntimeSettingsSnapshot settings, DaSourceRuntimeSettings source)
    {
        if (WorkerPlacement.WorkerKeyFor(source) is { } workerKey)
        {
            IWorkerHost host = workerHost_
                ?? throw new InvalidOperationException(
                    $"Worker isolation is configured for source '{source.SourceId}' but no worker supervisor is available.");
            return new WorkerSourceClient(host, workerKey, source.SourceId);
        }

        return base.Create(settings, source);
    }
}
