using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Drivers.Melsec;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class MelsecFactoryTests
{
    private static DaSourceRuntimeSettings MelsecSource(string sourceId = "a3n", string? serialPort = "/dev/ttyUSB0") =>
        SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = sourceId,
            SourceType = SourceTypes.MelsecA3n,
            SerialPortName = serialPort,
            UpdateRateMs = 1000
        }, 1000);

    private static DaSourceRuntimeSettings OpcDaSource(string sourceId = "da1") =>
        SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = sourceId,
            SourceType = SourceTypes.OpcDa,
            ProgId = "Mimics.HmiScada",
            UpdateRateMs = 1000
        }, 1000);

    private static DaRuntimeSettingsSnapshot Snapshot(params DaSourceRuntimeSettings[] sources) =>
        new(1000, true, sources, 1);

    [Fact]
    public void Create_MelsecA3n_ReturnsMelsecA3nClient()
    {
        var factory = new DaClientFactory();
        DaSourceRuntimeSettings source = MelsecSource();
        IDaClient client = factory.Create(Snapshot(source), source);
        Assert.IsType<MelsecA3nClient>(client);
    }

    [Fact]
    public void Create_OpcDa_ReturnsOpcDaClient()
    {
        var factory = new DaClientFactory();
        DaSourceRuntimeSettings source = OpcDaSource();
        IDaClient client = factory.Create(Snapshot(source), source);
        Assert.IsType<OpcDaClient>(client);
    }

    [Fact]
    public void Create_MelsecA3n_SourceTypeIsCaseInsensitive()
    {
        var factory = new DaClientFactory();
        DaSourceRuntimeSettings source = MelsecSource();
        // Lowercase SourceType still routes to MelsecA3nClient.
        source = source with { SourceType = "melseca3n" };
        IDaClient client = factory.Create(Snapshot(source), source);
        Assert.IsType<MelsecA3nClient>(client);
    }

    [Fact]
    public void Create_MelsecA3n_PropagatesSerialPortAndSourceId()
    {
        var factory = new DaClientFactory();
        DaSourceRuntimeSettings source = MelsecSource(sourceId: "plc1", serialPort: "/dev/ttyS3");
        var melsec = Assert.IsType<MelsecA3nClient>(factory.Create(Snapshot(source), source));
        // SourceId is observable via connect failure path; assert via reflection on the
        // production ctor's options field to avoid opening a real serial port.
        var optionsField = typeof(MelsecA3nClient).GetField("_options", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(optionsField);
        var options = Assert.IsType<MelsecA3nClientOptions>(optionsField!.GetValue(melsec));
        Assert.Equal("plc1", options.SourceId);
        Assert.Equal("/dev/ttyS3", options.SerialPortName);
    }
}
