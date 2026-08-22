using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class MappingGroupTests
{
    // MappingStore persists to AppContext.BaseDirectory/mappings.json (the test
    // bin folder) and prefers disk over seeded options — clean it per test.
    public MappingGroupTests()
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "mappings.json");
            if (File.Exists(p)) File.Delete(p);
        }
        catch { /* best effort */ }
    }

    private static MappingStore NewStore(params TagMapping[] seed)
    {
        var opts = Options.Create(new BridgeOptions { Mappings = seed.ToList() });
        return new MappingStore(opts);
    }

    private static TagMapping Tag(string itemId, string? daGroup = null, int rate = 0) => new()
    {
        SourceId = "default",
        ItemId = itemId,
        UaNodeId = "ns=2;s=" + itemId,
        DisplayName = itemId,
        PollRateMs = rate,
        DaGroup = daGroup
    };

    [Fact]
    public void DaGroup_SurvivesSeedAndTryUpdate()
    {
        // Regression guard for the redesign: Normalize() dropped DaGroup, so any
        // add/update wiped the tag's group membership.
        MappingStore store = NewStore(Tag("t2", daGroup: "g0"));
        (IReadOnlyList<TagMapping> snap, _) = store.GetSnapshot();
        Assert.Equal("g0", snap.Single(m => m.ItemId == "t2").DaGroup);

        MappingStore store2 = NewStore(Tag("t1"));
        Assert.True(store2.TryUpdate(Tag("t1", daGroup: "g1", rate: 250), out _));
        (IReadOnlyList<TagMapping> snap2, _) = store2.GetSnapshot();
        Assert.Equal("g1", snap2.Single(m => m.ItemId == "t1").DaGroup);
    }

    [Fact]
    public void RenameDaGroup_UpdatesReferencesOnly()
    {
        MappingStore store = NewStore(Tag("a", "old", 250), Tag("b", "old", 250), Tag("c", "other", 100), Tag("d"));
        int updated = store.RenameDaGroup("default", "old", "new");
        Assert.Equal(2, updated);
        (IReadOnlyList<TagMapping> snap, _) = store.GetSnapshot();
        Assert.Equal("new", snap.Single(m => m.ItemId == "a").DaGroup);
        Assert.Equal("new", snap.Single(m => m.ItemId == "b").DaGroup);
        Assert.Equal("other", snap.Single(m => m.ItemId == "c").DaGroup);
        Assert.Null(snap.Single(m => m.ItemId == "d").DaGroup);
    }

    [Fact]
    public void ClearDaGroup_FallsBackToSourceDefault()
    {
        MappingStore store = NewStore(Tag("a", "g1", 250), Tag("b", "g1", 250), Tag("c", null, 100));
        int updated = store.ClearDaGroup("default", "g1");
        Assert.Equal(2, updated);
        (IReadOnlyList<TagMapping> snap, _) = store.GetSnapshot();
        TagMapping a = snap.Single(m => m.ItemId == "a");
        Assert.Null(a.DaGroup);
        Assert.Equal(0, a.PollRateMs); // Source Default fallback
        Assert.Null(snap.Single(m => m.ItemId == "b").DaGroup);
        Assert.Equal(100, snap.Single(m => m.ItemId == "c").PollRateMs);
    }

    [Fact]
    public void SyncDaGroupRate_AlignsMemberTagsToGroupRate()
    {
        MappingStore store = NewStore(Tag("a", "g1", 1000), Tag("b", "g1", 1000), Tag("c", null, 100));
        int updated = store.SyncDaGroupRate("default", "g1", 500);
        Assert.Equal(2, updated);
        (IReadOnlyList<TagMapping> snap, _) = store.GetSnapshot();
        Assert.Equal(500, snap.Single(m => m.ItemId == "a").PollRateMs);
        Assert.Equal(500, snap.Single(m => m.ItemId == "b").PollRateMs);
        Assert.Equal("g1", snap.Single(m => m.ItemId == "a").DaGroup);
        Assert.Equal(100, snap.Single(m => m.ItemId == "c").PollRateMs);
    }
}
