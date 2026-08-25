using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// PlcGroups storage on DaSourceRuntimeSettings: legacy configs default to empty and
/// PlcGroupsEqual compares order-insensitively by (name CI, rate) — the worker restart
/// fingerprint (spec §5).
/// </summary>
public sealed class PlcGroupSettingsTests
{
    private static DaSourceRuntimeSettings MxSource(IReadOnlyList<PlcGroupSettings>? groups = null)
        => new("mx1", "MX", SourceTypes.MxComponent, 1000, true, 50000,
            null, null, null, null,
            new MxComponentSourceOptions(0, 3000, 2),
            IoMode: "AutoDetect", PlcGroups: groups);

    [Fact]
    public void LegacyConstruction_PlcGroupsList_IsEmpty()
    {
        // Positional call WITHOUT the new parameter must still compile (back-compat guarantee).
        DaSourceRuntimeSettings legacy = new("mx1", "MX", SourceTypes.MxComponent, 1000, true, 50000,
            null, null, null, null, new MxComponentSourceOptions(0, 3000, 2));
        Assert.Empty(legacy.PlcGroupsList);
    }

    [Fact]
    public void PlcGroupsEqual_SameMembersDifferentOrder_True()
    {
        DaSourceRuntimeSettings a = MxSource(new[] { new PlcGroupSettings("Fast", 250), new PlcGroupSettings("Slow", 5000) });
        DaSourceRuntimeSettings b = MxSource(new[] { new PlcGroupSettings("slow", 5000), new PlcGroupSettings("FAST", 250) });
        Assert.True(a.PlcGroupsEqual(b));
        Assert.True(b.PlcGroupsEqual(a));
    }

    [Theory]
    [InlineData(250, 500)]   // rate differs
    [InlineData(1, 1)]       // count differs handled below; here same-count different names
    public void PlcGroupsEqual_DifferentDefinitions_False(int rateA, int rateB)
    {
        DaSourceRuntimeSettings a = MxSource(new[] { new PlcGroupSettings("Fast", rateA) });
        DaSourceRuntimeSettings b = MxSource(new[] { new PlcGroupSettings(rateA == rateB ? "Other" : "Fast", rateB) });
        Assert.False(a.PlcGroupsEqual(b));
    }

    [Fact]
    public void PlcGroupsEqual_EmptyVsNull_True()
    {
        Assert.True(MxSource(null).PlcGroupsEqual(MxSource(Array.Empty<PlcGroupSettings>())));
    }

    [Fact]
    public void ToDto_FromDto_RoundTripsPlcGroups_ForMxSources()
    {
        DaSourceRuntimeSettings source = MxSource(new[]
        {
            new PlcGroupSettings("Fast", 250),
            new PlcGroupSettings("Slow", 5000)
        });

        SourceConfigDto dto = SourceConfigMigration.ToDto(source);
        Assert.NotNull(dto.PlcGroups);
        Assert.Equal(2, dto.PlcGroups!.Count);

        DaSourceRuntimeSettings restored = SourceConfigMigration.FromDto(dto, 1000);
        Assert.True(restored.PlcGroupsEqual(source));
    }

    [Fact]
    public void FromDto_ClearsPlcGroups_ForNonMxSources()
    {
        SourceConfigDto dto = new()
        {
            SourceId = "da1",
            SourceType = SourceTypes.OpcDa,
            ProgId = "Server.1",
            Host = "localhost",
            PlcGroups = new List<PlcGroupDto> { new() { Name = "Fast", UpdateRateMs = 250 } }
        };

        DaSourceRuntimeSettings restored = SourceConfigMigration.FromDto(dto, 1000);
        Assert.Empty(restored.PlcGroupsList);
    }

    [Fact]
    public void NormalizePlcGroups_TrimsDedupesClampsAndDropsBlanks()
    {
        IReadOnlyList<PlcGroupSettings> normalized = SourceConfigMigration.NormalizePlcGroups(new[]
        {
            new PlcGroupSettings("  Fast ", 1),     // clamped to 100
            new PlcGroupSettings("fast", 999),      // duplicate CI — first wins (100)
            new PlcGroupSettings("   ", 500),       // blank dropped
            new PlcGroupSettings("Slow", 0)         // clamped to 100
        });

        Assert.Equal(2, normalized.Count);
        Assert.Equal("Fast", normalized[0].Name);
        Assert.Equal(100, normalized[0].UpdateRateMs);
        Assert.Equal("Slow", normalized[1].Name);
        Assert.Equal(100, normalized[1].UpdateRateMs);
    }

    [Fact]
    public void ShouldRestartPollersForPlcGroups_DefinitionChange_True_ElseFalse()
    {
        DaSourceRuntimeSettings applied = MxSource(new[] { new PlcGroupSettings("Fast", 250) });

        Assert.True(BridgeWorker.ShouldRestartPollersForPlcGroups(applied, MxSource(new[] { new PlcGroupSettings("Fast", 400) })));
        Assert.True(BridgeWorker.ShouldRestartPollersForPlcGroups(applied, MxSource(Array.Empty<PlcGroupSettings>())));
        Assert.True(BridgeWorker.ShouldRestartPollersForPlcGroups(MxSource(null), MxSource(new[] { new PlcGroupSettings("New", 100) })));

        // Unrelated settings churn must NOT trigger a restart.
        Assert.False(BridgeWorker.ShouldRestartPollersForPlcGroups(applied, MxSource(new[] { new PlcGroupSettings("Fast", 250) })));
    }
}
