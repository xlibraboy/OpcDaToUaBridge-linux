using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class BridgeStateDisconnectTests
{
    private static BridgeState CreateState() => new(Options.Create(new BridgeOptions()));

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

    [Fact]
    public void PausedSource_IsNotFaultedInTheAggregate()
    {
        // A paused source is intentionally offline (issue #5), not a failure: the
        // aggregate must stay clean when nothing else is wrong.
        BridgeState state = CreateState();
        state.Configure(1000, 0, new[]
        {
            new DaSourceRuntimeSettings(
                "mx1", "MX1", SourceTypes.MxComponent, 1000, true, 50000,
                null, null, null, null, new MxComponentSourceOptions(0, 3000, 2))
        });

        state.SetSourceConnectionState("mx1", "Paused");

        BridgeRuntimeStatus status = state.GetStatus();
        Assert.Equal("Paused", status.DaConnectionState);
        Assert.Equal("Paused", status.Sources.Single(s => s.SourceId == "mx1").ConnectionState);
    }

    [Fact]
    public void PausedSource_BesideAConnectedOne_KeepsAggregateConnected()
    {
        BridgeState state = CreateState();
        state.Configure(1000, 0, new[]
        {
            new DaSourceRuntimeSettings(
                "mx1", "MX1", SourceTypes.MxComponent, 1000, true, 50000,
                null, null, null, null, new MxComponentSourceOptions(0, 3000, 2)),
            new DaSourceRuntimeSettings(
                "da1", "DA1", SourceTypes.OpcDa, 1000, true, 50000,
                new OpcDaSourceOptions("ProgId", "localhost", null, null, null), null, null, null, null)
        });

        state.SetSourceConnectionState("mx1", "Paused");
        state.SetSourceConnectionState("da1", "Connected");

        Assert.Equal("Connected", state.GetStatus().DaConnectionState);
    }

    [Fact]
    public void PausedSource_KeepingAFaultedOne_FaultedWins()
    {
        BridgeState state = CreateState();
        state.Configure(1000, 0, new[]
        {
            new DaSourceRuntimeSettings(
                "mx1", "MX1", SourceTypes.MxComponent, 1000, true, 50000,
                null, null, null, null, new MxComponentSourceOptions(0, 3000, 2)),
            new DaSourceRuntimeSettings(
                "da1", "DA1", SourceTypes.OpcDa, 1000, true, 50000,
                new OpcDaSourceOptions("ProgId", "localhost", null, null, null), null, null, null, null)
        });

        state.SetSourceConnectionState("mx1", "Paused");
        state.SetSourceConnectionState("da1", "Faulted");

        Assert.Equal("Faulted", state.GetStatus().DaConnectionState);
    }

    [Fact]
    public void ClearSourceError_RemovesStaleFaultTextWithoutTouchingState()
    {
        BridgeState state = CreateState();
        state.Configure(1000, 0, new[]
        {
            new DaSourceRuntimeSettings(
                "mx1", "MX1", SourceTypes.MxComponent, 1000, true, 50000,
                null, null, null, null, new MxComponentSourceOptions(0, 3000, 2))
        });
        state.SetSourceConnectionState("mx1", "Connected");
        state.SetSourceError("mx1", new InvalidOperationException("boom"));

        state.ClearSourceError("mx1");

        DaSourceStatusSnapshot source = state.GetStatus().Sources.Single(s => s.SourceId == "mx1");
        Assert.Null(source.LastError);
        Assert.Equal("Connected", source.ConnectionState);
    }
}
