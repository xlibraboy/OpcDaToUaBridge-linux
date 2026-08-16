using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class S7SourceSettingsTests
{
    [Fact]
    public void FromDto_S7200Ppi_MapsNestedSerialAndPpiFields()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "s7-1",
            SourceType = "S7200Ppi",
            DisplayName = "Line S7",
            UpdateRateMs = 1000,
            MaxMappedTags = 500,
            S7200 = new S7200PpiSourceOptionsDto
            {
                Transport = "Serial",
                SerialPortName = "/dev/ttyUSB1",
                BaudRate = 9600,
                DataBits = 8,
                Parity = "Even",
                StopBits = "One",
                LocalPpiAddress = 0,
                RemotePpiAddress = 2,
                TimeoutMs = 3000,
                RetryCount = 2
            }
        }, 1000);

        Assert.Equal(SourceTypes.S7200Ppi, source.SourceType);
        Assert.Equal("/dev/ttyUSB1", source.SerialPortName);
        Assert.Equal(9600, source.BaudRate);
        Assert.Equal("Even", source.Parity);
        Assert.Equal(0, source.LocalPpiAddress);
        Assert.Equal(2, source.RemotePpiAddress);
        Assert.NotNull(source.S7200);
        Assert.Equal(500, source.MaxMappedTags);
    }

    [Fact]
    public void FromDto_S7200Ppi_AppliesDefaultsWhenNestedSparse()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "s7-2",
            SourceType = SourceTypes.S7200Ppi,
            SerialPortName = "/dev/ttyUSB0" // flat fallback OK if nested missing serial only
        }, 1000);

        Assert.Equal(SourceTypes.S7200Ppi, source.SourceType);
        Assert.Equal(9600, source.BaudRate);
        Assert.Equal(8, source.DataBits);
        Assert.Equal("Even", source.Parity);
        Assert.Equal("One", source.StopBits);
        Assert.Equal(0, source.LocalPpiAddress);
        Assert.Equal(2, source.RemotePpiAddress);
        Assert.Equal(3000, source.TimeoutMs);
        Assert.Equal(2, source.RetryCount);
    }

    [Fact]
    public void Normalize_UnknownSourceType_BecomesOpcDa()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "x",
            SourceType = "UnknownDriver"
        }, 1000);
        Assert.Equal(SourceTypes.OpcDa, source.SourceType);
    }

    [Fact]
    public void ToDto_S7200Ppi_RoundTripsNestedOptions()
    {
        var original = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "s7-rt",
            SourceType = SourceTypes.S7200Ppi,
            DisplayName = "RT",
            S7200 = new S7200PpiSourceOptionsDto
            {
                SerialPortName = "/dev/ttyUSB2",
                BaudRate = 19200,
                Parity = "Even",
                LocalPpiAddress = 1,
                RemotePpiAddress = 3
            }
        }, 1000);

        SourceConfigDto dto = SourceConfigMigration.ToDto(original);
        Assert.Equal(SourceTypes.S7200Ppi, dto.SourceType);
        Assert.NotNull(dto.S7200);
        Assert.Equal("/dev/ttyUSB2", dto.S7200!.SerialPortName);
        Assert.Equal(19200, dto.S7200.BaudRate);
        Assert.Equal(1, dto.S7200.LocalPpiAddress);
        Assert.Equal(3, dto.S7200.RemotePpiAddress);

        var again = SourceConfigMigration.FromDto(dto, 1000);
        Assert.Equal(original.LocalPpiAddress, again.LocalPpiAddress);
        Assert.Equal(original.RemotePpiAddress, again.RemotePpiAddress);
        Assert.Equal(original.SerialPortName, again.SerialPortName);
    }

    [Fact]
    public void FromDto_S7200Ppi_PreservesExplicitRemotePpiAddressZero()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "s7-0",
            SourceType = SourceTypes.S7200Ppi,
            S7200 = new S7200PpiSourceOptionsDto
            {
                SerialPortName = "/dev/ttyUSB0",
                LocalPpiAddress = 0,
                RemotePpiAddress = 0
            }
        }, 1000);

        Assert.Equal(0, source.RemotePpiAddress);
        Assert.Equal(0, source.LocalPpiAddress);
    }
}
