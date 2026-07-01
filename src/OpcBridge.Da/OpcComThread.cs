using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace OpcBridge.Da;

/// <summary>
/// Owns a dedicated STA thread that serializes all COM calls for a single OPC DA source.
/// Legacy OPC DA servers are apartment-sensitive; pinning COM work to one STA thread
/// avoids marshalling failures under subscription callbacks.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class OpcComThread : IDisposable
{
    private readonly Thread thread_;
    private readonly BlockingCollection<Action> queue_ = new();
    private readonly ManualResetEventSlim idle_ = new(initialState: true);
    private bool disposed_;
    private int queued_items_;
    private long last_action_ticks_utc_;

    public OpcComThread(string name)
    {
        thread_ = new(ThreadLoop)
        {
            Name = name,
            IsBackground = true
        };
        thread_.SetApartmentState(ApartmentState.STA);
    }

    public void Start()
    {
        if (disposed_)
        {
            throw new ObjectDisposedException(nameof(OpcComThread));
        }

        thread_.Start();
    }

    /// <summary>
    /// Enqueues an action and blocks the caller until it has executed on the STA thread.
    /// </summary>
    public void EnqueueAndWait(Action action)
    {
        if (disposed_)
        {
            throw new ObjectDisposedException(nameof(OpcComThread));
        }

        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref queued_items_);
        queue_.Add(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Enqueues a function and blocks the caller until it has executed on the STA thread,
    /// returning the function's result.
    /// </summary>
    public T EnqueueAndWait<T>(Func<T> function)
    {
        if (disposed_)
        {
            throw new ObjectDisposedException(nameof(OpcComThread));
        }

        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref queued_items_);
        queue_.Add(() =>
        {
            try
            {
                tcs.SetResult(function());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;

        try
        {
            queue_.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (thread_.IsAlive)
        {
            thread_.Join();
        }

        queue_.Dispose();
        idle_.Dispose();
    }

    private void ThreadLoop()
    {
        foreach (Action action in queue_.GetConsumingEnumerable())
        {
            idle_.Reset();
            try
            {
                action();
            }
            finally
            {
                Interlocked.Decrement(ref queued_items_);
                Interlocked.Exchange(ref last_action_ticks_utc_, DateTime.UtcNow.Ticks);
                idle_.Set();
            }
        }
    }

    public (bool Alive, int QueuedItems, DateTime? LastActionUtc) GetStats()
    {
        bool alive = !disposed_ && thread_.IsAlive;
        int queued = Interlocked.CompareExchange(ref queued_items_, 0, 0);
        long ticks = Interlocked.Read(ref last_action_ticks_utc_);
        DateTime? lastAction = ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
        return (alive, queued, lastAction);
    }
}
