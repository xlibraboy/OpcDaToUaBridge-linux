using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Every snapshot carries two stamps: the source-preferred value time
/// (<see cref="BridgeValueSnapshot.TimestampUtc"/>) and the bridge's receive time
/// (<see cref="BridgeValueSnapshot.ServerTimestampUtc"/>) shown as the dashboard's
/// Server Timestamp.
/// </summary>
public sealed class BridgeStateServerTimestampTests
{
    private static BridgeState CreateState() => new(Options.Create(new BridgeOptions()));

    [Fact]
    public void SetValue_StampsBridgeReceiveTimeBesideSourceTime()
    {
        BridgeState state = CreateState();
        DateTime sourceStamp = DateTime.UtcNow.AddMinutes(-5);
        DateTime before = DateTime.UtcNow;

        state.SetValue(new BridgeValue("ua-a", "tag", 1.0, sourceStamp, 192, true));

        DateTime after = DateTime.UtcNow;
        Assert.True(state.TryGetSnapshot("ua-a", "tag", out BridgeValueSnapshot snapshot));
        Assert.Equal(sourceStamp, snapshot.TimestampUtc);
        Assert.InRange(snapshot.ServerTimestampUtc, before, after);
    }

    [Fact]
    public void UpdateDaRead_StampsOneReceiveTimeForTheWholeBatch()
    {
        BridgeState state = CreateState();
        DateTime sourceStamp = DateTime.UtcNow.AddSeconds(-30);

        state.UpdateDaRead(
            "da-a",
            new[]
            {
                new BridgeValue("da-a", "tag1", 1.0, sourceStamp, 192, true),
                new BridgeValue("da-a", "tag2", 2.0, sourceStamp, 192, true)
            },
            TimeSpan.FromMilliseconds(5));

        DateTime after = DateTime.UtcNow;
        Assert.True(state.TryGetSnapshot("da-a", "tag1", out BridgeValueSnapshot first));
        Assert.True(state.TryGetSnapshot("da-a", "tag2", out BridgeValueSnapshot second));
        Assert.Equal(first.ServerTimestampUtc, second.ServerTimestampUtc);
        Assert.True(first.ServerTimestampUtc > sourceStamp);
        Assert.True(first.ServerTimestampUtc <= after);
    }
}
