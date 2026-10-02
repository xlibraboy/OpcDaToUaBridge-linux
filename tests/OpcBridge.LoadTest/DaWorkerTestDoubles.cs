using OpcBridge.App;
using OpcBridge.Client.Workers;

namespace OpcBridge.LoadTest;

/// <summary>Shared doubles for the worker tests: a host that hands out a preset channel and
/// counts releases, and a channel that answers every request with its own payload.</summary>
internal sealed class TestWorkerHost : IWorkerHost
{
    private int _released;

    public TestWorkerHost(IWorkerChannel? channel = null) => Channel = channel;

    public IWorkerChannel? Channel { get; set; }

    public int Released => Volatile.Read(ref _released);

    public Task<IWorkerChannel> EnsureChannelAsync(string workerKey, CancellationToken cancellationToken)
        => Channel is null
            ? Task.FromException<IWorkerChannel>(new InvalidOperationException($"No channel for '{workerKey}'."))
            : Task.FromResult(Channel);

    public void ReleaseClient(string workerKey) => Interlocked.Increment(ref _released);
}

internal sealed class NullWorkerChannel : IWorkerChannel
{
    public event Action<WorkerFrame>? PushReceived
    {
        add { }
        remove { }
    }

    public event Action<Exception?>? Closed
    {
        add { }
        remove { }
    }

    public Exception? ClosedReason => null;

    public Task<WorkerFrame> RequestAsync(string type, object payload, TimeSpan timeout, CancellationToken cancellationToken)
        => Task.FromResult(WorkerFrame.Create(type, 1, payload));
}
