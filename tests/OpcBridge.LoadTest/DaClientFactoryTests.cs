using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DaClientFactoryTests
{
    private static DaSourceRuntimeSettings UaSource() => new(
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
        Melsec: null,
        S7200: null,
        MxComponent: null);

    [Fact]
    public void Create_OpcUa_WithLoggerFactory_WiresLoggerIntoClient()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var factory = new SourceClientFactory(loggerFactory);
        var snapshot = new DaRuntimeSettingsSnapshot(1000, true, new[] { UaSource() }, 1);

        ISourceClient client = factory.Create(snapshot, UaSource());

        FieldInfo field = typeof(OpcUaSourceClient).GetField("logger_", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("OpcUaSourceClient.logger_ field not found");
        object logger = field.GetValue(client)
            ?? throw new InvalidOperationException("OpcUaSourceClient.logger_ is null");
        Assert.False(ReferenceEquals(logger, NullLogger.Instance), "UA client must not receive NullLogger");
    }

    [Fact]
    public void Create_OpcUa_WithoutLoggerFactory_FallsBackToNullLogger()
    {
        var factory = new SourceClientFactory();
        var snapshot = new DaRuntimeSettingsSnapshot(1000, true, new[] { UaSource() }, 1);

        ISourceClient client = factory.Create(snapshot, UaSource());

        FieldInfo field = typeof(OpcUaSourceClient).GetField("logger_", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("OpcUaSourceClient.logger_ field not found");
        object logger = field.GetValue(client)
            ?? throw new InvalidOperationException("OpcUaSourceClient.logger_ is null");
        Assert.True(ReferenceEquals(logger, NullLogger.Instance));
    }

    [Fact]
    public void Create_OpcUa_ReturnsOpcUaSourceClient()
    {
        var factory = new SourceClientFactory();
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
            Melsec: null,
            S7200: null,
            MxComponent: null);
        var snapshot = new DaRuntimeSettingsSnapshot(1000, true, new[] { source }, 1);

        ISourceClient client = factory.Create(snapshot, source);

        Assert.IsType<OpcUaSourceClient>(client);
    }

    [Fact]
    public void Create_OpcDa_ReturnsOpcDaClient()
    {
        var factory = new SourceClientFactory();
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
            Melsec: null,
            S7200: null,
            MxComponent: null);
        var snapshot = new DaRuntimeSettingsSnapshot(1000, true, new[] { source }, 1);

        ISourceClient client = factory.Create(snapshot, source);

        Assert.IsType<OpcDaClient>(client);
    }

    private static DaClientOptions GetDaOptions(DaSourceRuntimeSettings source, bool globalUseSubscriptions)
    {
        var factory = new SourceClientFactory();
        var snapshot = new DaRuntimeSettingsSnapshot(1000, globalUseSubscriptions, new[] { source }, 1);
        ISourceClient client = factory.Create(snapshot, source);
        return ((OpcDaClient)client).Options;
    }

    private static DaSourceRuntimeSettings DaSource(string ioMode) => new(
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
        Melsec: null,
        S7200: null,
        MxComponent: null,
        IoMode: ioMode);

    [Fact]
    public void Create_OpcDa_IoModeSync_DisablesSubscriptions()
    {
        Assert.False(GetDaOptions(DaSource("Sync"), globalUseSubscriptions: true).UseSubscriptions);
    }

    [Fact]
    public void Create_OpcDa_IoModeAutoDetect_FollowsGlobalSwitch()
    {
        Assert.True(GetDaOptions(DaSource("AutoDetect"), globalUseSubscriptions: true).UseSubscriptions);
        Assert.False(GetDaOptions(DaSource("AutoDetect"), globalUseSubscriptions: false).UseSubscriptions);
    }

    [Fact]
    public void Create_OpcDa_IoModeAsync20_ForcesSubscriptions()
    {
        Assert.True(GetDaOptions(DaSource("Async20"), globalUseSubscriptions: false).UseSubscriptions);
    }

    [Fact]
    public void Create_OpcDa_IoModeAsync20_CarriesModeToClient()
    {
        Assert.Equal("Async20", GetDaOptions(DaSource("Async20"), globalUseSubscriptions: true).IoMode);
        Assert.Equal("Sync", GetDaOptions(DaSource("Sync"), globalUseSubscriptions: true).IoMode);
    }

    [Fact]
    public void Create_OpcDa_CarriesGroupIoModesToClient()
    {
        var source = DaSource("AutoDetect") with
        {
            OpcDa = new OpcDaSourceOptions(
                "Matrikon.OPC.Simulation.1",
                "localhost",
                null,
                null,
                null,
                new DaGroupIoMode[] { new("OpcBridge_500", 500, "Sync"), new("OpcBridge_1000", 1000, "Async20") })
        };

        DaClientOptions options = GetDaOptions(source, globalUseSubscriptions: true);

        Assert.Equal("Sync", options.GroupIoModes[500]);
        Assert.Equal("Async20", options.GroupIoModes[1000]);
        Assert.Equal(2, options.GroupIoModes.Count);
    }
}
