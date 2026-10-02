using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class BridgeStateDisconnectTests
{
    private static BridgeState CreateState() => new(Options.Create(new BridgeOptions()));

    [Fact]
    public void Configure_KeepsLiveConnectionStateOfHealthySource_WhileAnotherRetries()
    {
        BridgeState state = CreateState();
        DaSourceRuntimeSettings[] sources =
        [
            CreateDaSource("default"),
            CreateDaSource("mxopc-plc-mhi")
        ];

        state.Configure(1000, 0, sources);
        state.SetSourceConnectionState("default", "Connected");
        state.SetSourceConnectionState("mxopc-plc-mhi", "Reconnecting");

        // The coordinator re-runs Configure on every retry tick while one source is
        // down; a healthy source must not blink back to "Disconnected" (#35).
        state.Configure(1000, 0, sources);

        IReadOnlyList<DaSourceStatusSnapshot> statuses = state.GetStatus().Sources;
        Assert.Equal("Connected", statuses.Single(source => source.SourceId == "default").ConnectionState);
        Assert.Equal("Reconnecting", statuses.Single(source => source.SourceId == "mxopc-plc-mhi").ConnectionState);
        Assert.Equal("Partial", state.GetStatus().DaConnectionState);
    }

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

    [Fact]
    public void GetBadQualityTags_ReturnsOnlyBadQualityValues()
    {
        BridgeState state = CreateState();
        state.SetValue(new BridgeValue("ua-a", "goodTag", 1.0, DateTime.UtcNow, 192, true));
        state.SetValue(new BridgeValue("ua-a", "badTag", null, DateTime.UtcNow, 0, false));
        state.SetValue(new BridgeValue("ua-b", "otherGood", 2.0, DateTime.UtcNow, 192, true));

        IReadOnlyList<(string SourceId, string ItemId)> bad = state.GetBadQualityTags();

        Assert.Single(bad);
        Assert.Equal("ua-a", bad[0].SourceId);
        Assert.Equal("badTag", bad[0].ItemId);
    }

    [Fact]
    public void GetBadQualityTags_EmptyWhenAllValuesGood()
    {
        BridgeState state = CreateState();
        state.SetValue(new BridgeValue("ua-a", "goodTag", 1.0, DateTime.UtcNow, 192, true));

        Assert.Empty(state.GetBadQualityTags());
    }
}
