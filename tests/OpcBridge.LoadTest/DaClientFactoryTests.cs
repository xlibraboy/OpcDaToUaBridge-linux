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
            ProgId: string.Empty,
            Host: string.Empty,
            RemoteUsername: null,
            RemotePassword: null,
            RemoteDomain: null,
            Transport: "Serial",
            SerialPortName: string.Empty,
            BaudRate: 9600,
            DataBits: 8,
            Parity: "Odd",
            StopBits: "One",
            StationNo: "00",
            PcNo: "FF",
            TimeoutMs: 3000,
            RetryCount: 2,
            EndpointUrl: "opc.tcp://kepware:49320",
            SecurityMode: "None",
            SecurityPolicy: "None",
            UaUsername: null,
            UaPassword: null,
            SessionTimeoutMs: 60000,
            ReconnectDelayMs: 5000,
            MaxMappedTags: 50000,
            UseSubscriptions: true,
            UpdateRateMs: 1000);
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
            ProgId: "Matrikon.OPC.Simulation.1",
            Host: "localhost",
            RemoteUsername: null,
            RemotePassword: null,
            RemoteDomain: null,
            Transport: "Serial",
            SerialPortName: string.Empty,
            BaudRate: 9600,
            DataBits: 8,
            Parity: "Odd",
            StopBits: "One",
            StationNo: "00",
            PcNo: "FF",
            TimeoutMs: 3000,
            RetryCount: 2,
            EndpointUrl: string.Empty,
            SecurityMode: "None",
            SecurityPolicy: "None",
            UaUsername: null,
            UaPassword: null,
            SessionTimeoutMs: 60000,
            ReconnectDelayMs: 5000,
            MaxMappedTags: 50000,
            UseSubscriptions: true,
            UpdateRateMs: 500);
        var snapshot = new DaRuntimeSettingsSnapshot(1000, true, new[] { source }, 1);

        IDaClient client = factory.Create(snapshot, source);

        Assert.IsType<OpcDaClient>(client);
    }
}
