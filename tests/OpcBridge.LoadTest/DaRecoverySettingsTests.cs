using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DaRecoverySettingsTests
{
    [Fact]
    public void FromDto_OpcUaMissingWatchdog_UsesDefault60000()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "ua1",
            SourceType = SourceTypes.OpcUa,
            OpcUa = new OpcUaSourceOptionsDto
            {
                EndpointUrl = "opc.tcp://host:4840/opcuasim/",
                SecurityMode = "None",
                SecurityPolicy = "None"
            }
        }, defaultUpdateRate: 1000);

        Assert.NotNull(source.OpcUa);
        Assert.Equal(60000, source.OpcUa.WatchdogTimeoutMs);
        Assert.Equal(60000, source.WatchdogTimeoutMs);
    }

    [Fact]
    public void FromDto_OpcUaNestedWatchdog_RoundTrip()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "ua1",
            SourceType = SourceTypes.OpcUa,
            OpcUa = new OpcUaSourceOptionsDto
            {
                EndpointUrl = "opc.tcp://host:4840/opcuasim/",
                SecurityMode = "None",
                SecurityPolicy = "None",
                WatchdogTimeoutMs = 120000
            }
        }, defaultUpdateRate: 1000);

        Assert.Equal(120000, source.OpcUa!.WatchdogTimeoutMs);
        Assert.Equal(120000, source.WatchdogTimeoutMs);
    }

    [Fact]
    public void FromDto_OpcUaFlatWatchdog_RoundTrip()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "ua1",
            SourceType = SourceTypes.OpcUa,
            EndpointUrl = "opc.tcp://host:4840/opcuasim/",
            SecurityMode = "None",
            SecurityPolicy = "None",
            WatchdogTimeoutMs = 0
        }, defaultUpdateRate: 1000);

        Assert.Equal(0, source.OpcUa!.WatchdogTimeoutMs);
        Assert.Equal(0, source.WatchdogTimeoutMs);
    }

    [Fact]
    public void Normalize_ClampsWatchdog()
    {
        DaSourceRuntimeSettings negative = new(
            "ua1",
            "UA1",
            SourceTypes.OpcUa,
            1000,
            true,
            50000,
            null,
            new OpcUaSourceOptions("opc.tcp://host:4840/", "None", "None", null, null, 60000, 5000, -5),
            null,
            null,
            null);
        DaSourceRuntimeSettings normalized = SourceConfigMigration.Normalize(negative, 1000);

        Assert.Equal(0, normalized.OpcUa!.WatchdogTimeoutMs);
        Assert.Equal(0, normalized.WatchdogTimeoutMs);
    }

    [Fact]
    public void ToDto_PersistsWatchdog()
    {
        DaSourceRuntimeSettings source = new(
            "ua1",
            "UA1",
            SourceTypes.OpcUa,
            1000,
            true,
            50000,
            null,
            new OpcUaSourceOptions("opc.tcp://host:4840/", "None", "None", null, null, 60000, 5000, 30000),
            null,
            null,
            null);

        SourceConfigDto dto = SourceConfigMigration.ToDto(source);

        Assert.NotNull(dto.OpcUa);
        Assert.Equal(30000, dto.OpcUa!.WatchdogTimeoutMs);
    }
}
