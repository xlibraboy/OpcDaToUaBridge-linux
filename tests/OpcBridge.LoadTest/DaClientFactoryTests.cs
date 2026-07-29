using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DaClientFactoryTests
{
    [Fact]
    public void Create_OpcUa_ReturnsOpcUaSourceClient()
    {
        var factory = new DaClientFactory();
        var source = new DaSourceRuntimeSettings(
            SourceId: "kep",
            DisplayName: "Kepware",
            SourceType: SourceTypes.OpcUa,
            UpdateRateMs: 1000,
            UseSubscriptions: true,
            MaxMappedTags: 50000,
            OpcDa: null,
            OpcUa: new OpcUaSourceOptions(
                "opc.tcp://kepware:49320",
                "None",
                "None",
                null,
                null,
                60000,
                5000),
            Melsec: null);
        var snapshot = new DaRuntimeSettingsSnapshot(1000, true, new[] { source }, 1);

        IDaClient client = factory.Create(snapshot, source);

        Assert.IsType<OpcUaSourceClient>(client);
    }

    [Fact]
    public void Create_OpcDa_ReturnsOpcDaClient()
    {
        var factory = new DaClientFactory();
        var source = new DaSourceRuntimeSettings(
            SourceId: "line1",
            DisplayName: "Line 1",
            SourceType: SourceTypes.OpcDa,
            UpdateRateMs: 500,
            UseSubscriptions: true,
            MaxMappedTags: 50000,
            OpcDa: new OpcDaSourceOptions(
                "Matrikon.OPC.Simulation.1",
                "localhost",
                null,
                null,
                null),
            OpcUa: null,
            Melsec: null);
        var snapshot = new DaRuntimeSettingsSnapshot(1000, true, new[] { source }, 1);

        IDaClient client = factory.Create(snapshot, source);

        Assert.IsType<OpcDaClient>(client);
    }
}
