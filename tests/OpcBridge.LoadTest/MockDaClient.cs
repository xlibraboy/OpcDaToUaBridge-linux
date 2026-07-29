using OpcBridge.Core;
using OpcBridge.Da;

namespace OpcBridge.LoadTest;

/// <summary>
/// In-memory <see cref="ISourceClient"/> that synthesizes N values per read at a configurable rate.
/// Runs on any platform (no COM). Used to stress-test <see cref="BridgeState"/> and the write queue
/// without a real OPC DA server.
/// </summary>
public sealed class MockDaClient : ISourceClient, ISubscribableSourceClient
{
    private readonly int tagCount;
    private readonly string sourceId;
    private long readCounter;

    /// <summary>
    /// Present for interface compliance; MockDaClient is poll-only and never raises this.
    /// </summary>
    public event Action<IReadOnlyList<BridgeValue>>? ValuesReceived
    {
        add { }
        remove { }
    }

    public MockDaClient(string sourceId, int tagCount)
    {
        this.sourceId = sourceId;
        this.tagCount = tagCount;
    }

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<BridgeValue>> ReadAsync(IReadOnlyList<TagMapping> mappings, CancellationToken cancellationToken)
    {
        int count = Math.Min(tagCount, mappings.Count);
        BridgeValue[] values = new BridgeValue[count];
        long counter = Interlocked.Increment(ref readCounter);
        double baseValue = counter % 1000;

        for (int i = 0; i < count; i++)
        {
            TagMapping m = mappings[i];
            values[i] = new BridgeValue(sourceId, m.ItemId, baseValue + i * 0.001, DateTime.UtcNow, 192, true);
        }

        return Task.FromResult<IReadOnlyList<BridgeValue>>(values);
    }

    public Task<bool> WriteAsync(string itemId, object? value, CancellationToken cancellationToken)
    {
        // Simulate a successful write to the DA server.
        return Task.FromResult(value is not null);
    }

    public bool TryGetTagMetadata(string itemId, out short? canonicalDataType, out int? accessRights)
    {
        canonicalDataType = 5;
        accessRights = 3;
        return !string.IsNullOrWhiteSpace(itemId);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
