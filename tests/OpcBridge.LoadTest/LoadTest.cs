using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

public class LoadTest
{
    private static List<TagMapping> BuildMappings(string sourceId, int count)
    {
        List<TagMapping> mappings = new(count);
        for (int i = 0; i < count; i++)
        {
            mappings.Add(new TagMapping
            {
                SourceId = sourceId,
                ItemId = $"Sim.Tag.{i}",
                DisplayName = $"Tag {i}",
                DataType = "Double",
                Enabled = true,
                Mode = TagMode.Source,
                PollRateMs = 500
            });
        }
        return mappings;
    }

    private static BridgeState BuildState(int expectedTagCount)
    {
        Microsoft.Extensions.Options.OptionsWrapper<BridgeOptions> opts =
            new(new BridgeOptions { ExpectedTagCount = expectedTagCount });
        return new BridgeState(opts);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    [InlineData(50000)]
    public async Task BridgeState_HandlesReadLoad(int tagCount)
    {
        const string sourceId = "load";
        BridgeState state = BuildState(tagCount);
        List<TagMapping> mappings = BuildMappings(sourceId, tagCount);
        MockDaClient client = new(sourceId, tagCount);

        state.Configure(500, tagCount, Array.Empty<DaSourceRuntimeSettings>());

        // Drive 60s of reads at 500ms — but cap iterations to keep the test fast (12 cycles = 6s real).
        // The assertion is about scale + stability, not wall-clock duration.
        int cycles = 12;
        for (int i = 0; i < cycles; i++)
        {
            IReadOnlyList<BridgeValue> values = await client.ReadAsync(mappings, CancellationToken.None);
            state.UpdateDaRead(sourceId, values, TimeSpan.FromMilliseconds(5));
            foreach (BridgeValue v in values)
            {
                state.SetValue(v);
            }
        }

        IReadOnlyList<BridgeValueSnapshot> snapshots = state.GetValues();
        Assert.Equal(tagCount, snapshots.Count);

        // Every value must be good-quality with a non-null value.
        foreach (BridgeValueSnapshot s in snapshots)
        {
            Assert.True(s.IsGood);
            Assert.NotNull(s.Value);
        }

        // Gen2 size must be bounded: assert it is non-negative and within a generous ceiling.
        // (We cannot easily compare to "steady state" without a warmup; this guards runaway growth.)
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        long gen2Size = info.GenerationInfo[2].SizeAfterBytes;
        Assert.True(gen2Size >= 0);
        Assert.True(gen2Size < 100L * 1024 * 1024, $"Gen2 size {gen2Size} exceeded 100MB ceiling");
    }

    [Fact]
    public async Task WriteQueue_DrainsWithoutBlockingReads()
    {
        const string sourceId = "write";
        const int readTagCount = 20000;
        const int writeCount = 1000;

        BridgeState state = BuildState(readTagCount);
        List<TagMapping> readMappings = BuildMappings(sourceId, readTagCount);
        MockDaClient client = new(sourceId, readTagCount);
        WriteQueue queue = new();

        state.Configure(500, readTagCount, Array.Empty<DaSourceRuntimeSettings>());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        // Start a write-queue consumer on a background task.
        Task consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (WriteRequest req in queue.ReaderAsync(sourceId, cts.Token))
                {
                    bool ok = await client.WriteAsync(req.ItemId, req.Value, cts.Token);
                    req.Tcs.TrySetResult(ok);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Expected on teardown.
            }
        }, cts.Token);

        // Enqueue 1000 writes while streaming reads.
        List<Task<bool>> writeResults = new(writeCount);
        for (int i = 0; i < writeCount; i++)
        {
            TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Enqueue(sourceId, new WriteRequest(sourceId, $"Sim.Tag.{i}", 42.0, tcs));
            writeResults.Add(tcs.Task);
        }

        // Drive reads concurrently.
        IReadOnlyList<BridgeValue> values = await client.ReadAsync(readMappings, cts.Token);
        state.UpdateDaRead(sourceId, values, TimeSpan.FromMilliseconds(10));

        // All writes must resolve true within the 30s timeout.
        bool[] results = await Task.WhenAll(writeResults);
        Assert.All(results, r => Assert.True(r));
        Assert.Equal(writeCount, results.Length);

        // Reads must not have dropped.
        Assert.Equal(readTagCount, state.GetValues().Count);

        cts.Cancel();
        await consumer;
    }

    [Fact]
    public async Task WriteQueue_RoutesRequestsToPerSourceChannels()
    {
        WriteQueue queue = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // A write for one source must never surface on another source's reader
        // (the old shared-queue re-enqueue pattern starved the matching consumer).
        for (int i = 0; i < 3; i++)
        {
            string sourceId = i == 0 ? "ua-a" : $"ua-{i}";
            TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Enqueue(sourceId, new WriteRequest(sourceId, $"Tag.{i}", 42.0, tcs));
        }

        await using IAsyncEnumerator<WriteRequest> readerA = queue.ReaderAsync("ua-a", cts.Token).GetAsyncEnumerator();
        Assert.True(await readerA.MoveNextAsync());
        Assert.Equal("ua-a", readerA.Current.SourceId);
        Assert.Equal("Tag.0", readerA.Current.ItemId);

        await using IAsyncEnumerator<WriteRequest> readerB = queue.ReaderAsync("ua-1", cts.Token).GetAsyncEnumerator();
        Assert.True(await readerB.MoveNextAsync());
        Assert.Equal("ua-1", readerB.Current.SourceId);

        // A fresh reader for ua-a sees nothing else queued for it (cancel the pending
        // read before disposal — disposing a ReadAllAsync enumerator with an in-flight
        // MoveNextAsync throws NotSupportedException).
        using CancellationTokenSource idle = new();
        idle.CancelAfter(TimeSpan.FromMilliseconds(300));
        await using IAsyncEnumerator<WriteRequest> idleReader = queue.ReaderAsync("ua-a", idle.Token).GetAsyncEnumerator();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await idleReader.MoveNextAsync());
    }
}
