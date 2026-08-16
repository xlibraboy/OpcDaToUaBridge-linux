using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Drivers.S7;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class S7FactoryTests
{
    private static DaSourceRuntimeSettings S7Source(string sourceId = "s7", string? serialPort = "/dev/ttyUSB0") =>
        SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = sourceId,
            SourceType = SourceTypes.S7200Ppi,
            SerialPortName = serialPort,
            UpdateRateMs = 1000,
            S7200 = new S7200PpiSourceOptionsDto
            {
                SerialPortName = serialPort,
                BaudRate = 9600,
                Parity = "Even",
                LocalPpiAddress = 0,
                RemotePpiAddress = 2
            }
        }, 1000);

    private static DaRuntimeSettingsSnapshot Snapshot(params DaSourceRuntimeSettings[] sources) =>
        new(1000, true, sources, 1);

    [Fact]
    public void Create_S7200Ppi_ReturnsS7200Client()
    {
        var factory = new SourceClientFactory();
        DaSourceRuntimeSettings source = S7Source();
        ISourceClient client = factory.Create(Snapshot(source), source);
        Assert.IsType<S7200Client>(client);
    }

    [Fact]
    public void Create_S7200Ppi_SourceTypeIsCaseInsensitive()
    {
        var factory = new SourceClientFactory();
        DaSourceRuntimeSettings source = S7Source() with { SourceType = "s7200ppi" };
        ISourceClient client = factory.Create(Snapshot(source), source);
        Assert.IsType<S7200Client>(client);
    }

    [Fact]
    public void Create_S7200Ppi_PropagatesSerialAndPpiAddresses()
    {
        var factory = new SourceClientFactory();
        DaSourceRuntimeSettings source = S7Source(sourceId: "plc7", serialPort: "/dev/ttyS3");
        source = source with
        {
            S7200 = source.S7200! with { LocalPpiAddress = 1, RemotePpiAddress = 3, SerialPortName = "/dev/ttyS3" }
        };

        var client = Assert.IsType<S7200Client>(factory.Create(Snapshot(source), source));
        var optionsField = typeof(S7200Client).GetField(
            "_options",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(optionsField);
        var options = Assert.IsType<S7200ClientOptions>(optionsField!.GetValue(client));
        Assert.Equal("plc7", options.SourceId);
        Assert.Equal("/dev/ttyS3", options.SerialPortName);
        Assert.Equal(1, options.LocalPpiAddress);
        Assert.Equal(3, options.RemotePpiAddress);
        Assert.Equal("Even", options.Parity);
    }
}
