using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// UpsertPlcGroup/RemovePlcGroup validation: MX-sources-only gate, name rules, 100 ms clamp,
/// 16-group soft cap, version bumps. Mirrors UpsertUaSubscription semantics (spec §4).
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class PlcGroupRegistryTests : IDisposable
{
    private readonly string _sourcesJsonPath;

    public PlcGroupRegistryTests()
    {
        _sourcesJsonPath = Path.Combine(AppContext.BaseDirectory, "sources.json");
        if (File.Exists(_sourcesJsonPath))
        {
            File.Delete(_sourcesJsonPath);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_sourcesJsonPath))
        {
            File.Delete(_sourcesJsonPath);
        }
    }

    private static (DaRuntimeSettings Settings, string SourceId) CreateWithMxSource()
    {
        // Same temp persistence + IOptions<DaClientOptions> construction used by
        // DaGroupIoModeTests/BridgeAppDiscoveryTests; sources.json lives under
        // AppContext.BaseDirectory and is deleted by the ctor/Dispose above.
        DaRuntimeSettings settings = new(Options.Create(new DaClientOptions()));

        settings.UpsertSource(new DaSourceRuntimeSettings(
            "mx1",
            "MX1",
            SourceTypes.MxComponent,
            1000,
            true,
            50000,
            OpcDa: null,
            OpcUa: null,
            Melsec: null,
            S7200: null,
            MxComponent: new MxComponentSourceOptions(0, 3000, 2)));

        settings.UpsertSource(new DaSourceRuntimeSettings(
            "da1",
            "DA1",
            SourceTypes.OpcDa,
            1000,
            true,
            50000,
            OpcDa: new OpcDaSourceOptions("Server.1", "localhost", null, null, null),
            OpcUa: null,
            Melsec: null,
            S7200: null,
            MxComponent: null));

        return (settings, "mx1");
    }

    [Fact]
    public void Upsert_AddsAndUpdatesCaseInsensitively_ClampsRate_BumpsVersion()
    {
        (DaRuntimeSettings settings, string sourceId) = CreateWithMxSource();
        long v0 = settings.GetSnapshot().Version;

        settings.UpsertPlcGroup(sourceId, "  Fast ", 1);
        DaRuntimeSettingsSnapshot afterAdd = settings.GetSnapshot();
        Assert.Single(afterAdd.GetSource(sourceId)!.PlcGroupsList);
        Assert.Equal("Fast", afterAdd.GetSource(sourceId)!.PlcGroupsList[0].Name);
        Assert.Equal(100, afterAdd.GetSource(sourceId)!.PlcGroupsList[0].UpdateRateMs);
        Assert.True(afterAdd.Version > v0);

        settings.UpsertPlcGroup(sourceId, "fast", 5000);
        DaRuntimeSettingsSnapshot afterUpdate = settings.GetSnapshot();
        Assert.Single(afterUpdate.GetSource(sourceId)!.PlcGroupsList);
        Assert.Equal(5000, afterUpdate.GetSource(sourceId)!.PlcGroupsList[0].UpdateRateMs);
    }

    [Fact]
    public void Upsert_RejectsNonMxSource()
    {
        (DaRuntimeSettings settings, _) = CreateWithMxSource();
        // Seed/create or reuse an existing non-MX source id from the fixture (e.g. "da1").
        ArgumentException ex = Assert.Throws<ArgumentException>(() => settings.UpsertPlcGroup("da1", "Fast", 250));
        Assert.Contains("MX Component", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Upsert_RejectsUnknownSource_AndBlankNames()
    {
        (DaRuntimeSettings settings, string sourceId) = CreateWithMxSource();
        Assert.Throws<ArgumentException>(() => settings.UpsertPlcGroup("nope", "Fast", 250));
        Assert.Throws<ArgumentException>(() => settings.UpsertPlcGroup(sourceId, "   ", 250));
    }

    [Fact]
    public void Upsert_EnforcesSixteenGroupCap()
    {
        (DaRuntimeSettings settings, string sourceId) = CreateWithMxSource();
        for (int i = 0; i < SourceConfigMigration.MaxPlcGroupsPerSource; i++)
        {
            settings.UpsertPlcGroup(sourceId, $"G{i:00}", 100 * (i + 1));
        }

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => settings.UpsertPlcGroup(sourceId, "Overflow", 250));
        Assert.Contains("maximum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_DeletesDefinition_AndThrowsWhenMissing()
    {
        (DaRuntimeSettings settings, string sourceId) = CreateWithMxSource();
        settings.UpsertPlcGroup(sourceId, "Fast", 250);
        settings.RemovePlcGroup(sourceId, "fast");
        Assert.Empty(settings.GetSnapshot().GetSource(sourceId)!.PlcGroupsList);
        Assert.Throws<ArgumentException>(() => settings.RemovePlcGroup(sourceId, "Fast"));
    }
}
