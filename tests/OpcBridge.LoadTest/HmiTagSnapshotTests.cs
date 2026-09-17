using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.App.Hmi;
using OpcBridge.Client;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(InterlinkApiAppCollection))]
public sealed class HmiTagSnapshotTests
{
    // MappingStore persists to AppContext.BaseDirectory/mappings.json (the test
    // bin folder) and prefers disk over seeded options — clean it per test.
    public HmiTagSnapshotTests()
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "mappings.json");
            if (File.Exists(p)) File.Delete(p);
        }
        catch { /* best effort */ }
    }

    private static MappingStore NewStore(params TagMapping[] seed) =>
        new(Options.Create(new BridgeOptions { Mappings = seed.ToList() }));

    private static BridgeState NewState() => new(Options.Create(new BridgeOptions()));

    private static DaSourceRuntimeSettings Source(string sourceId, string displayName, string sourceType) =>
        new(sourceId, displayName, sourceType, 1000, true, 50000, null, null, null, null, null);

    private static TagMapping Tag(string sourceId, string itemId) => new()
    {
        SourceId = sourceId,
        ItemId = itemId,
        DisplayName = itemId,
        Enabled = true
    };

    [Fact]
    public void Build_CarriesEachTagsSourceNameAndType()
    {
        MappingStore store = NewStore(
            Tag("default", "T1"),
            Tag("ua-a", "T2"));

        HmiTagsResponse response = HmiTagSnapshot.Build(
            store,
            NewState(),
            new DaRuntimeSettingsSnapshot(
                DaRuntimeSettings.FixedUpdateRateMs,
                true,
                [Source("default", "Default Source", SourceTypes.OpcDa), Source("ua-a", "Line A", SourceTypes.OpcUa)],
                1));

        HmiTagDto daTag = response.Tags.Single(t => t.ItemId == "T1");
        Assert.Equal("default", daTag.SourceId);
        Assert.Equal("Default Source", daTag.SourceName);
        Assert.Equal(SourceTypes.OpcDa, daTag.SourceType);

        HmiTagDto uaTag = response.Tags.Single(t => t.ItemId == "T2");
        Assert.Equal("ua-a", uaTag.SourceId);
        Assert.Equal("Line A", uaTag.SourceName);
        Assert.Equal(SourceTypes.OpcUa, uaTag.SourceType);
    }

    [Fact]
    public void Build_MappingOnRemovedSource_KeepsSourceIdAndReportsNoType()
    {
        MappingStore store = NewStore(Tag("gone", "T1"));

        HmiTagsResponse response = HmiTagSnapshot.Build(
            store,
            NewState(),
            new DaRuntimeSettingsSnapshot(
                DaRuntimeSettings.FixedUpdateRateMs,
                true,
                [Source("default", "Default Source", SourceTypes.OpcDa)],
                1));

        HmiTagDto tag = Assert.Single(response.Tags);
        Assert.Equal("gone", tag.SourceId);
        Assert.Equal("gone", tag.SourceName);
        Assert.Equal(string.Empty, tag.SourceType);
    }
}
