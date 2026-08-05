using System.Threading.Channels;
using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Routes UA→source writes to a per-source bounded channel. Each source has exactly one
/// consumer task reading its own channel, so requests are never re-enqueued across sources.
/// (A shared channel with cross-source re-enqueue starves the matching consumer: with N
/// readers a single request ping-ponged tens of thousands of times before resolution.)
/// </summary>
internal sealed class WriteQueue
{
    private const int ChannelCapacity = 1024;

    private readonly object gate_ = new();
    private readonly Dictionary<string, Channel<WriteRequest>> channels_ = new(StringComparer.OrdinalIgnoreCase);
    private long total_enqueued_;
    private long total_succeeded_;
    private long total_failed_;

    private Channel<WriteRequest> ChannelFor(string sourceId)
    {
        lock (gate_)
        {
            if (!channels_.TryGetValue(sourceId, out Channel<WriteRequest>? channel))
            {
                channel = Channel.CreateBounded<WriteRequest>(
                    new BoundedChannelOptions(ChannelCapacity)
                    {
                        FullMode = BoundedChannelFullMode.DropWrite,
                        SingleReader = false,
                        SingleWriter = false
                    });
                channels_[sourceId] = channel;
            }

            return channel;
        }
    }

    /// <summary>Enqueue a write for one source. Never blocks; drops when that source's queue is full.</summary>
    public void Enqueue(string sourceId, WriteRequest request)
    {
        Interlocked.Increment(ref total_enqueued_);
        _ = ChannelFor(sourceId).Writer.TryWrite(request);
    }

    public IAsyncEnumerable<WriteRequest> ReaderAsync(string sourceId, CancellationToken cancellationToken)
    {
        return ChannelFor(sourceId).Reader.ReadAllAsync(cancellationToken);
    }

    public void RecordResult(bool success)
    {
        if (success)
        {
            Interlocked.Increment(ref total_succeeded_);
        }
        else
        {
            Interlocked.Increment(ref total_failed_);
        }
    }

    public (int CurrentDepth, long TotalEnqueued, long TotalSucceeded, long TotalFailed) GetStats()
    {
        int depth = 0;
        lock (gate_)
        {
            foreach (Channel<WriteRequest> channel in channels_.Values)
            {
                depth += channel.Reader.Count;
            }
        }

        return (
            depth,
            Interlocked.Read(ref total_enqueued_),
            Interlocked.Read(ref total_succeeded_),
            Interlocked.Read(ref total_failed_));
    }
}

internal sealed record WriteRequest(
    string SourceId,
    string ItemId,
    object? Value,
    TaskCompletionSource<bool> Tcs);
