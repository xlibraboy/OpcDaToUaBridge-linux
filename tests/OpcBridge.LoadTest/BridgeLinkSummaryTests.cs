using OpcBridge.Hmi.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The status strip's aggregate: the worst live link state wins, and the detail line names
/// the bridges behind it. This is what replaced the old latched "Connected".
/// </summary>
public sealed class BridgeLinkSummaryTests
{
    [Fact]
    public void NoBridges_ReadsDisconnected()
    {
        (BridgeLinkAggregate state, string label, string detail) = BridgeLinkSummary.Build([]);

        Assert.Equal(BridgeLinkAggregate.Disconnected, state);
        Assert.Equal("Disconnected", label);
        Assert.Equal("Not connected", detail);
    }

    [Fact]
    public void SingleConnectedBridge_ReadsConnected()
    {
        var statuses = new[] { new BridgeLinkStatus("default", BridgeLinkState.Connected, null) };

        (BridgeLinkAggregate state, string label, string detail) = BridgeLinkSummary.Build(statuses);

        Assert.Equal(BridgeLinkAggregate.Connected, state);
        Assert.Equal("Connected", label);
        Assert.Equal("1 bridge connected", detail);
    }

    [Fact]
    public void AllConnected_ReadsConnected_WithCount()
    {
        var statuses = new[]
        {
            new BridgeLinkStatus("a", BridgeLinkState.Connected, null),
            new BridgeLinkStatus("b", BridgeLinkState.Connected, null)
        };

        (BridgeLinkAggregate state, string label, string detail) = BridgeLinkSummary.Build(statuses);

        Assert.Equal(BridgeLinkAggregate.Connected, state);
        Assert.Equal("Connected", label);
        Assert.Equal("2/2 bridges connected", detail);
    }

    [Fact]
    public void AnyReconnecting_ReadsReconnecting_AndNamesThem()
    {
        var statuses = new[]
        {
            new BridgeLinkStatus("a", BridgeLinkState.Connected, null),
            new BridgeLinkStatus("b", BridgeLinkState.Reconnecting, "socket closed")
        };

        (BridgeLinkAggregate state, string label, string detail) = BridgeLinkSummary.Build(statuses);

        Assert.Equal(BridgeLinkAggregate.Reconnecting, state);
        Assert.Equal("Reconnecting", label);
        Assert.Equal("1/2 bridges connected — reconnecting: b", detail);
    }

    [Fact]
    public void FailedBridge_ReadsFailed_WithTheErrorText()
    {
        var statuses = new[]
        {
            new BridgeLinkStatus("a", BridgeLinkState.Connected, null),
            new BridgeLinkStatus("b", BridgeLinkState.Failed, "connection refused")
        };

        (BridgeLinkAggregate state, string label, string detail) = BridgeLinkSummary.Build(statuses);

        Assert.Equal(BridgeLinkAggregate.Failed, state);
        Assert.Equal("Failed", label);
        Assert.Equal("1/2 bridges connected — failed: b (connection refused)", detail);
    }
}
