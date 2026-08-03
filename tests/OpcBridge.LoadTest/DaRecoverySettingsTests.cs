using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DaRecoverySettingsTests
{
    [Fact]
    public void FromDto_OpcDaMissingRecoveryFields_UsesDefaults()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "line1",
            SourceType = SourceTypes.OpcDa,
            ProgId = "Matrikon.OPC.Simulation.1",
            Host = "localhost",
            UseSubscriptions = true
        }, defaultUpdateRate: 1000);

        Assert.NotNull(source.OpcDa);
        Assert.Equal(1, source.OpcDa.MaxConsecutiveFailures);
        Assert.Equal(60000, source.OpcDa.WatchdogTimeoutMs);
        Assert.Equal(1, source.MaxConsecutiveFailures);
        Assert.Equal(60000, source.WatchdogTimeoutMs);
    }

    [Fact]
    public void FromDto_OpcDaFlatRecoveryFields_RoundTrip()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "line1",
            SourceType = SourceTypes.OpcDa,
            ProgId = "Kepware.KEPServerEX.V6",
            Host = "plc-host",
            MaxConsecutiveFailures = 4,
            WatchdogTimeoutMs = 120000
        }, defaultUpdateRate: 1000);

        Assert.NotNull(source.OpcDa);
        Assert.Equal(4, source.OpcDa.MaxConsecutiveFailures);
        Assert.Equal(120000, source.OpcDa.WatchdogTimeoutMs);
    }

    [Fact]
    public void FromDto_OpcDaNestedWatchdogZero_StaysDisabled()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "line1",
            SourceType = SourceTypes.OpcDa,
            OpcDa = new OpcDaSourceOptionsDto
            {
                ProgId = "Kepware.KEPServerEX.V6",
                Host = "plc-host",
                MaxConsecutiveFailures = 3,
                WatchdogTimeoutMs = 0
            }
        }, defaultUpdateRate: 1000);

        Assert.NotNull(source.OpcDa);
        Assert.Equal(3, source.OpcDa.MaxConsecutiveFailures);
        Assert.Equal(0, source.OpcDa.WatchdogTimeoutMs);
    }

    [Fact]
    public void Normalize_ClampsRecoveryValues()
    {
        var source = SourceConfigMigration.Normalize(new DaSourceRuntimeSettings(
            "line1",
            "Line 1",
            SourceTypes.OpcDa,
            1000,
            true,
            50000,
            new OpcDaSourceOptions(
                "Matrikon.OPC.Simulation.1",
                "localhost",
                null,
                null,
                null,
                MaxConsecutiveFailures: 0,
                WatchdogTimeoutMs: -5),
            null,
            null,
            null), defaultUpdateRate: 1000);

        Assert.Equal(1, source.OpcDa!.MaxConsecutiveFailures);
        Assert.Equal(0, source.OpcDa.WatchdogTimeoutMs); // negative → disabled
    }

    [Fact]
    public void ToDto_PersistsRecoveryFields()
    {
        var source = new DaSourceRuntimeSettings(
            "line1",
            "Line 1",
            SourceTypes.OpcDa,
            1000,
            true,
            50000,
            new OpcDaSourceOptions(
                "Matrikon.OPC.Simulation.1",
                "localhost",
                null,
                null,
                null,
                3,
                120000),
            null,
            null,
            null);

        SourceConfigDto dto = SourceConfigMigration.ToDto(source);

        Assert.NotNull(dto.OpcDa);
        Assert.Equal(3, dto.OpcDa.MaxConsecutiveFailures);
        Assert.Equal(120000, dto.OpcDa.WatchdogTimeoutMs);
    }

    [Fact]
    public void NonDaSources_DefaultRecoveryKnobs()
    {
        var source = new DaSourceRuntimeSettings(
            "kep",
            "Kep",
            SourceTypes.OpcUa,
            1000,
            true,
            50000,
            null,
            new OpcUaSourceOptions("opc.tcp://host:4840", "None", "None", null, null, 60000, 5000),
            null,
            null);

        Assert.Equal(1, source.MaxConsecutiveFailures);
        Assert.Equal(60000, source.WatchdogTimeoutMs);
    }
}
