using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class MxComponentSourceSettingsTests
{
    [Fact]
    public void FromDto_MxComponent_NestedPreferred()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "mx1",
            SourceType = SourceTypes.MxComponent,
            UpdateRateMs = 1000,
            MaxMappedTags = 500,
            MxComponent = new MxComponentSourceOptionsDto
            {
                LogicalStationNumber = 3,
                TimeoutMs = 4000,
                RetryCount = 1
            },
            LogicalStationNumber = 9 // flat must lose to nested
        }, 1000);

        Assert.Equal(SourceTypes.MxComponent, source.SourceType);
        Assert.NotNull(source.MxComponent);
        Assert.Equal(3, source.MxComponent.LogicalStationNumber);
        Assert.Equal(4000, source.MxComponent.TimeoutMs);
        Assert.Equal(1, source.MxComponent.RetryCount);
        Assert.Equal(500, source.MaxMappedTags);
        Assert.Null(source.OpcDa);
    }

    [Fact]
    public void FromDto_MxComponent_FlatFallback()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "mx1",
            SourceType = SourceTypes.MxComponent,
            LogicalStationNumber = 2,
            TimeoutMs = 2500,
            RetryCount = 3,
            UpdateRateMs = 1000
        }, 1000);

        Assert.Equal(SourceTypes.MxComponent, source.SourceType);
        Assert.NotNull(source.MxComponent);
        Assert.Equal(2, source.MxComponent.LogicalStationNumber);
        Assert.Equal(2500, source.MxComponent.TimeoutMs);
        Assert.Equal(3, source.MxComponent.RetryCount);
    }

    [Fact]
    public void FromDto_NonMxSourceType_IgnoresFlatLogicalStation()
    {
        // A Melsec source with a stray flat logicalStationNumber must not create an MX nest.
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "a3n1",
            SourceType = SourceTypes.MelsecA3n,
            SerialPortName = "/dev/ttyUSB0",
            LogicalStationNumber = 4,
            UpdateRateMs = 1000
        }, 1000);

        Assert.Equal(SourceTypes.MelsecA3n, source.SourceType);
        Assert.Null(source.MxComponent);
        Assert.NotNull(source.Melsec);
    }

    [Fact]
    public void Normalize_MxComponent_ClampsStationAndDefaults()
    {
        var source = SourceConfigMigration.Normalize(new DaSourceRuntimeSettings(
            "mx1",
            "MX1",
            SourceTypes.MxComponent,
            1000,
            true,
            50000,
            null,
            null,
            null,
            null,
            new MxComponentSourceOptions(4096, 0, 0)), 1000);

        Assert.NotNull(source.MxComponent);
        // 4096 is outside the 0-1023 station range → 0; zero timeout/retry → defaults.
        Assert.Equal(0, source.MxComponent.LogicalStationNumber);
        Assert.Equal(3000, source.MxComponent.TimeoutMs);
        Assert.Equal(2, source.MxComponent.RetryCount);
    }

    [Fact]
    public void Normalize_MxComponent_KeepsValidValues()
    {
        var source = SourceConfigMigration.Normalize(new DaSourceRuntimeSettings(
            "mx1",
            "MX1",
            SourceTypes.MxComponent,
            1000,
            true,
            50000,
            null,
            null,
            null,
            null,
            new MxComponentSourceOptions(6, 5000, 4)), 1000);

        Assert.Equal(6, source.MxComponent!.LogicalStationNumber);
        Assert.Equal(5000, source.MxComponent.TimeoutMs);
        Assert.Equal(4, source.MxComponent.RetryCount);
    }

    [Fact]
    public void ToDto_RoundTripsMxComponent()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "mx1",
            SourceType = SourceTypes.MxComponent,
            LogicalStationNumber = 5,
            TimeoutMs = 3500,
            RetryCount = 2,
            UpdateRateMs = 1000
        }, 1000);

        SourceConfigDto dto = SourceConfigMigration.ToDto(source);
        Assert.NotNull(dto.MxComponent);
        Assert.Equal(5, dto.MxComponent!.LogicalStationNumber);
        Assert.Equal(3500, dto.MxComponent.TimeoutMs);
        Assert.Equal(2, dto.MxComponent.RetryCount);

        DaSourceRuntimeSettings restored = SourceConfigMigration.FromDto(dto, 1000);
        Assert.Equal(5, restored.MxComponent!.LogicalStationNumber);
        Assert.Equal(3500, restored.MxComponent.TimeoutMs);
        Assert.Equal(2, restored.MxComponent.RetryCount);
    }

    [Fact]
    public void CompatGetters_SurfaceMxValues()
    {
        var source = new DaSourceRuntimeSettings(
            "mx1",
            "MX1",
            SourceTypes.MxComponent,
            1000,
            true,
            50000,
            null,
            null,
            null,
            null,
            new MxComponentSourceOptions(4, 4200, 1));

        Assert.Equal(4, source.LogicalStationNumber);
        Assert.Equal(4200, source.MxComponentTimeoutMs);
        Assert.Equal(1, source.MxComponentRetryCount);
    }
}
