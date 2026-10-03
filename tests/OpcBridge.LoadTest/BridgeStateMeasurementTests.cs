using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Read/write telemetry comes from value-bearing updates only. Subscription callbacks pass
/// <see cref="TimeSpan.Zero"/> (there is no device read) and empty poll passes carry no
/// values; neither may reset the counters, blank the duration, or drop the clock offset the
/// last real update measured. The bridge/connection state still advances on every pass.
/// </summary>
public sealed class BridgeStateMeasurementTests
{
    private static BridgeState CreateState() => new(Options.Create(new BridgeOptions()));

    private static DaSourceRuntimeSettings CreateDaSource(string sourceId) => new(
        sourceId,
        sourceId,
        SourceTypes.OpcDa,
        1000,
        true,
        50000,
        new OpcDaSourceOptions("Test.Server.1", "localhost", null, null, null),
        null,
        null,
        null);

    private static BridgeValue Value(string itemId, bool good = true) =>
        new("da-a", itemId, 1.0, DateTime.UtcNow.AddMilliseconds(-500), good ? 192 : 0, good);

    [Fact]
    public void UpdateDaRead_EmptyBatch_PreservesLastReadStats()
    {
        BridgeState state = CreateState();
        state.Configure(1000, 1, new[] { CreateDaSource("da-a") });
        state.UpdateDaRead("da-a", new[] { Value("tag") }, TimeSpan.FromMilliseconds(5));

        BridgeRuntimeStatus before = state.GetStatus();
        DaSourceStatusSnapshot beforeSource = before.Sources.Single(s => s.SourceId == "da-a");
        Assert.NotNull(beforeSource.DaClockOffsetMs);

        state.UpdateDaRead("da-a", Array.Empty<BridgeValue>(), TimeSpan.FromMilliseconds(99));

        BridgeRuntimeStatus after = state.GetStatus();
        DaSourceStatusSnapshot afterSource = after.Sources.Single(s => s.SourceId == "da-a");
        Assert.Equal(before.LastDaReadUtc, after.LastDaReadUtc);
        Assert.Equal(before.LastDaReadCount, after.LastDaReadCount);
        Assert.Equal(beforeSource.LastDaReadUtc, afterSource.LastDaReadUtc);
        Assert.Equal(beforeSource.LastDaReadCount, afterSource.LastDaReadCount);
        Assert.Equal(beforeSource.LastDaReadDurationMs, afterSource.LastDaReadDurationMs);
        Assert.Equal(beforeSource.DaClockOffsetMs, afterSource.DaClockOffsetMs);
        Assert.Equal("Running", after.BridgeState);
    }

    [Fact]
    public void UpdateDaRead_SubscriptionBatch_KeepsLastReadDuration()
    {
        BridgeState state = CreateState();
        state.Configure(1000, 1, new[] { CreateDaSource("da-a") });
        state.UpdateDaRead("da-a", new[] { Value("tag") }, TimeSpan.FromMilliseconds(5));

        // Callback batches pass TimeSpan.Zero — the duration must stay the device-read measure.
        state.UpdateDaRead("da-a", new[] { Value("tag2") }, TimeSpan.Zero);

        DaSourceStatusSnapshot source = state.GetStatus().Sources.Single(s => s.SourceId == "da-a");
        Assert.Equal(5.0, source.LastDaReadDurationMs);
        Assert.Equal(1, source.LastDaReadCount);
        Assert.NotNull(source.LastDaReadUtc);
    }

    [Fact]
    public void UpdateDaRead_BadQualityBatch_KeepsLastClockOffset()
    {
        BridgeState state = CreateState();
        state.Configure(1000, 1, new[] { CreateDaSource("da-a") });
        state.UpdateDaRead("da-a", new[] { Value("tag") }, TimeSpan.FromMilliseconds(5));
        double? offset = state.GetStatus().Sources.Single(s => s.SourceId == "da-a").DaClockOffsetMs;
        Assert.NotNull(offset);

        state.UpdateDaRead("da-a", new[] { Value("tag", good: false) }, TimeSpan.FromMilliseconds(5));

        Assert.Equal(offset, state.GetStatus().Sources.Single(s => s.SourceId == "da-a").DaClockOffsetMs);
    }

    [Fact]
    public void MarkUaWrite_EmptyCycle_KeepsCountAndRate()
    {
        BridgeState state = CreateState();
        state.MarkUaWrite(3, TimeSpan.FromMilliseconds(4));
        BridgeRuntimeStatus before = state.GetStatus();

        state.MarkUaWrite(0, TimeSpan.FromMilliseconds(1));

        BridgeRuntimeStatus after = state.GetStatus();
        Assert.Equal(3, after.LastUaWriteCount);
        Assert.Equal(before.LastUaWriteUtc, after.LastUaWriteUtc);
        Assert.Equal(before.LastPollValueRate, after.LastPollValueRate);
        Assert.Equal(1.0, after.LastPollDurationMs);
    }

    [Fact]
    public void GetSourceValueFlow_TracksTotalAndPerSecond()
    {
        BridgeState state = CreateState();
        state.UpdateDaRead("da-a", new[] { Value("a"), Value("b"), Value("c") }, TimeSpan.FromMilliseconds(5));

        (long total, double perSecond) = state.GetSourceValueFlow();
        Assert.Equal(3, total);
        Assert.InRange(perSecond, 0.001, 3.0);

        // An empty pass carries no values and must not move the flow counters.
        state.UpdateDaRead("da-a", Array.Empty<BridgeValue>(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(3, state.GetSourceValueFlow().Total);
    }
}
