using System.Collections.Concurrent;
using OpcBridge.Client.Workers;

namespace OpcBridge.App;

/// <summary>Transport seam between the proxy clients and a worker process. Production uses a
/// <see cref="WorkerConnection"/> over the named pipe; tests drive the same type over an
/// in-memory duplex stream.</summary>
internal interface IWorkerChannel
{
    Task<WorkerFrame> RequestAsync(string type, object payload, TimeSpan timeout, CancellationToken cancellationToken);

    event Action<WorkerFrame>? PushReceived;

    /// <summary>
    /// Raised once when the pipe closes (worker exit, operator kill, transport failure).
    /// Proxies subscribe so a dead worker surfaces immediately instead of leaving the
    /// source looking connected until its next request happens to fail.
    /// </summary>
    event Action<Exception?>? Closed;

    /// <summary>Why the channel closed, or null while it is open.</summary>
    Exception? ClosedReason { get; }
}

/// <summary>
/// Parent side of the worker pipe: request/response correlation by frame sequence (only the
/// ack/result frame types count as responses; everything else is a push), serialized writes,
/// and a read loop that fails all pending requests when the pipe closes.
/// </summary>
internal sealed class WorkerConnection : IWorkerChannel, IAsyncDisposable
{
    internal static readonly HashSet<string> ResponseTypes = new(StringComparer.Ordinal)
    {
        WorkerFrameTypes.Ack,
        WorkerFrameTypes.ReadResult,
        WorkerFrameTypes.WriteResult,
        WorkerFrameTypes.MetadataResult,
        WorkerFrameTypes.BrowseResult,
        WorkerFrameTypes.Pong
    };

    private readonly Stream pipe_;
    private readonly SemaphoreSlim writeLock_ = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<WorkerFrame>> pending_ = new();
    private readonly CancellationTokenSource closed_ = new();
    private readonly Task readLoop_;
    private long nextSequence_;
    private int closedFlag_;

    public WorkerConnection(Stream pipe)
    {
        pipe_ = pipe;
        readLoop_ = Task.Run(ReadLoopAsync);
    }

    public Exception? ClosedReason { get; private set; }

    public event Action<WorkerFrame>? PushReceived;

    public event Action<Exception?>? Closed;

    public async Task<WorkerFrame> RequestAsync(
        string type,
        object payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        long sequence = Interlocked.Increment(ref nextSequence_);
        var completion = new TaskCompletionSource<WorkerFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending_[sequence] = completion;
        try
        {
            await WriteAsync(WorkerFrame.Create(type, sequence, payload), cancellationToken).ConfigureAwait(false);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                return await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Worker did not answer '{type}' within {timeout.TotalSeconds:0.#} s.");
            }
        }
        finally
        {
            pending_.TryRemove(sequence, out _);
        }
    }

    private async Task WriteAsync(WorkerFrame frame, CancellationToken cancellationToken)
    {
        await writeLock_.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(pipe_, frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock_.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? reason = null;
        try
        {
            while (true)
            {
                WorkerFrame? frame = await FrameCodec.ReadAsync(pipe_, closed_.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                if (ResponseTypes.Contains(frame.Type)
                    && pending_.TryRemove(frame.Seq, out TaskCompletionSource<WorkerFrame>? completion))
                {
                    completion.TrySetResult(frame);
                    continue;
                }

                PushReceived?.Invoke(frame);
            }
        }
        catch (WorkerProtocolException ex)
        {
            reason = ex;
        }
        catch (IOException ex)
        {
            reason = ex;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        Close(reason);
    }

    private void Close(Exception? reason)
    {
        if (Interlocked.Exchange(ref closedFlag_, 1) != 0)
        {
            return;
        }

        ClosedReason = reason;
        closed_.Cancel();

        var failure = reason ?? new IOException("Worker connection closed.");
        foreach (TaskCompletionSource<WorkerFrame> completion in pending_.Values)
        {
            completion.TrySetException(failure);
        }

        pending_.Clear();

        try
        {
            Closed?.Invoke(reason);
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        closed_.Cancel();
        try
        {
            await readLoop_.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await pipe_.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        writeLock_.Dispose();
        closed_.Dispose();
    }
}
