using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class MelsecSourceSettingsTests
{
    [Fact]
    public void FromDto_MissingSourceType_DefaultsToOpcDa()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "line1",
            ProgId = "Matrikon.OPC.Simulation.1",
            Host = "localhost",
            UpdateRateMs = 500
        }, defaultUpdateRate: 1000);

        Assert.Equal(SourceTypes.OpcDa, source.SourceType);
        Assert.Equal("Matrikon.OPC.Simulation.1", source.ProgId);
        Assert.Equal("", source.SerialPortName);
        Assert.Equal(50000, source.MaxMappedTags);
        Assert.Equal(9600, source.BaudRate);
        Assert.Equal("Odd", source.Parity);
        Assert.NotNull(source.OpcDa);
        Assert.Null(source.Melsec);
    }

    [Fact]
    public void FromDto_MelsecA3n_MapsSerialFields()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "a3n1",
            SourceType = "MelsecA3n",
            DisplayName = "Line A3N",
            SerialPortName = "/dev/ttyUSB0",
            BaudRate = 19200,
            DataBits = 8,
            Parity = "Odd",
            StopBits = "One",
            StationNo = "00",
            PcNo = "FF",
            TimeoutMs = 3000,
            RetryCount = 2,
            MaxMappedTags = 500,
            UpdateRateMs = 1000
        }, 1000);

        Assert.Equal(SourceTypes.MelsecA3n, source.SourceType);
        Assert.Equal("/dev/ttyUSB0", source.SerialPortName);
        Assert.Equal(19200, source.BaudRate);
        Assert.Equal("Serial", source.Transport);
        Assert.Equal(500, source.MaxMappedTags);
        Assert.NotNull(source.Melsec);
        Assert.Null(source.OpcDa);
        Assert.Null(source.OpcUa);
    }

    [Fact]
    public void FromDto_MelsecA3n_NestedPreferred()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "a3n1",
            SourceType = "MelsecA3n",
            UpdateRateMs = 1000,
            MaxMappedTags = 200,
            Melsec = new MelsecA3nSourceOptionsDto
            {
                Transport = "Serial",
                SerialPortName = "/dev/ttyNested",
                BaudRate = 38400,
                DataBits = 8,
                Parity = "Even",
                StopBits = "One",
                StationNo = "01",
                PcNo = "FF",
                TimeoutMs = 4000,
                RetryCount = 1
            },
            SerialPortName = "/dev/ttyFlat"
        }, 1000);

        Assert.Equal("/dev/ttyNested", source.SerialPortName);
        Assert.Equal(38400, source.BaudRate);
        Assert.Equal("Even", source.Parity);
        Assert.NotNull(source.Melsec);
        Assert.Null(source.OpcDa);
    }

    [Fact]
    public void Normalize_UnknownSourceType_BecomesOpcDa()
    {
        var source = SourceConfigMigration.Normalize(new DaSourceRuntimeSettings(
            "x",
            "X",
            "UnknownDriver",
            1000,
            true,
            2000,
            new OpcDaSourceOptions("", "localhost", null, null, null),
            null,
            null), 1000);
        Assert.Equal(SourceTypes.OpcDa, source.SourceType);
        Assert.NotNull(source.OpcDa);
        Assert.Null(source.OpcUa);
        Assert.Null(source.Melsec);
    }
}
