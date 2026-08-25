using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Regression tests for <see cref="BridgeWorker.SourceConnectionEquals"/>.
/// A UA source's UpdateRateMs drives the subscription PublishingInterval, which is
/// fixed when the client is created — so a rate change must be treated as a
/// connection change (session recreated) or the API reports success while values
/// keep arriving at the old cadence. DA sources build rate groups dynamically, so a
/// rate change is handled by the poller-restart path and must NOT reconnect.
/// </summary>
public sealed class SourceConnectionEqualsTests
{
    private static DaSourceRuntimeSettings UaSource(int updateRateMs) => new(
        SourceId: "ua1",
        DisplayName: "UA Demo",
        SourceType: SourceTypes.OpcUa,
        UpdateRateMs: updateRateMs,
        UseSubscriptions: true,
        MaxMappedTags: 50000,
        OpcDa: null,
        OpcUa: new OpcUaSourceOptions(
            "opc.tcp://host:4840/opcuasim/",
            "None",
            "None",
            null,
            null,
            SessionTimeoutMs: 60000,
            ReconnectDelayMs: 1000),
        Melsec: null,
        S7200: null,
        MxComponent: null);

    private static DaSourceRuntimeSettings DaSource(int updateRateMs) => new(
        SourceId: "da1",
        DisplayName: "DA Demo",
        SourceType: SourceTypes.OpcDa,
        UpdateRateMs: updateRateMs,
        UseSubscriptions: true,
        MaxMappedTags: 50000,
        OpcDa: new OpcDaSourceOptions(
            "Matrikon.OPC.Simulation.1",
            "localhost",
            null,
            null,
            null),
        OpcUa: null,
        Melsec: null,
        S7200: null,
        MxComponent: null);

    private static DaSourceRuntimeSettings MxSource(IReadOnlyList<PlcGroupSettings>? groups = null)
        => new("mx1", "MX", SourceTypes.MxComponent, 1000, true, 50000,
            null, null, null, null,
            new MxComponentSourceOptions(0, 3000, 2),
            IoMode: "AutoDetect", PlcGroups: groups);

    [Fact]
    public void ConnectionEquality_IgnoresPlcGroupChanges()
    {
        DaSourceRuntimeSettings applied = MxSource(new[] { new PlcGroupSettings("Fast", 250) });
        DaSourceRuntimeSettings regrouped = MxSource(new[] { new PlcGroupSettings("Slow", 5000) });

        // SourceConnectionEquals must remain true across ANY group edit (spec §5: no session reopen).
        Assert.True(BridgeWorker.SourceConnectionEquals(applied, regrouped));
    }

    [Fact]
    public void UaSource_RateChange_IsConnectionChange()
    {
        Assert.False(BridgeWorker.SourceConnectionEquals(UaSource(1000), UaSource(250)));
    }

    [Fact]
    public void UaSource_IdenticalSettings_AreEqual()
    {
        Assert.True(BridgeWorker.SourceConnectionEquals(UaSource(250), UaSource(250)));
    }

    [Fact]
    public void UaSource_EndpointChange_IsConnectionChange()
    {
        DaSourceRuntimeSettings a = UaSource(250);
        DaSourceRuntimeSettings b = a with
        {
            OpcUa = a.OpcUa! with { EndpointUrl = "opc.tcp://other:4840/opcuasim/" }
        };

        Assert.False(BridgeWorker.SourceConnectionEquals(a, b));
    }

    [Fact]
    public void DaSource_RateChange_IsNotConnectionChange()
    {
        // DA clients build rate groups per read cycle; a rate change only needs the
        // poller-restart path and must not tear down the COM session.
        Assert.True(BridgeWorker.SourceConnectionEquals(DaSource(1000), DaSource(250)));
    }
}
