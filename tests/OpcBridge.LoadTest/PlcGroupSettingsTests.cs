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
}
