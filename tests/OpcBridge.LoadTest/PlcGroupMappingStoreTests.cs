using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Group-rate-wins hygiene in the mapping store: batch reassign zeroes PollRateMs and
/// single-tag unassign via TryUpdate also drops the stale numeric override (spec §4).
/// </summary>
// Joins InterlinkApiAppCollection because MappingStore persists to (and the ctor of
// each store prefers) AppContext.BaseDirectory/mappings.json — same isolation rule
// as MappingGroupTests and the other store tests.
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class PlcGroupMappingStoreTests
{
    private static TagMapping Tag(string itemId, string plcGroup = "", int pollRateMs = 0)
        => new() { SourceId = "mx1", ItemId = itemId, UaNodeId = $"ns=2;s={itemId}", PlcGroup = plcGroup, PollRateMs = pollRateMs };

    // Exact fixture from MappingGroupTests: MappingStore persists to mappings.json in the
    // test bin folder and prefers disk over seeded options — clean it before constructing.
    private static MappingStore CreateStore()
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "mappings.json");
            if (File.Exists(p)) File.Delete(p);
        }
        catch { /* best effort */ }

        var opts = Options.Create(new BridgeOptions { Mappings = new List<TagMapping>() });
        return new MappingStore(opts);
    }

    [Fact]
    public void ReassignPlcGroup_MovesOnlyNamedMembers_ZeroesPollRate_SingleEvent()
    {
        MappingStore store = CreateStore(); // copy fixture from MappingGroupTests
        store.SetAll(new[] { Tag("D100", "Fast", 999), Tag("D101", "Fast"), Tag("D102", "Slow"), Tag("M0") });

        long beforeVersion = 0;
        int events = 0;
        store.Changed += _ => events++;

        int moved = store.ReassignPlcGroup("mx1", "fast"); // CI match

        Assert.Equal(2, moved);
        (IReadOnlyList<TagMapping> all, _) = store.GetSnapshot();
        Assert.All(all.Where(m => m.ItemId is "D100" or "D101"), m =>
        {
            Assert.Equal(string.Empty, m.PlcGroup);
            Assert.Equal(0, m.PollRateMs);      // D100's stale 999 dropped
        });
        Assert.Equal("Slow", all.First(m => m.ItemId == "D102").PlcGroup);
        Assert.Equal(1, events);                 // ONE Changed event
    }

    [Fact]
    public void ReassignPlcGroup_NoMatches_ReturnsZero_NoEvent()
    {
        MappingStore store = CreateStore();
        store.SetAll(new[] { Tag("D100", "Fast") });
        int events = 0;
        store.Changed += _ => events++;

        Assert.Equal(0, store.ReassignPlcGroup("mx1", "Missing"));
        Assert.Equal(0, events);
    }

    [Fact]
    public void TryUpdate_UnassigningGroup_ZeroesStalePollRate()
    {
        MappingStore store = CreateStore();
        store.SetAll(new[] { Tag("D100", "Fast", 750) });

        TagMapping edited = Tag("D100", plcGroup: "", pollRateMs: 750); // UI sends same numeric rate
        Assert.True(store.TryUpdate(edited, out _));

        (IReadOnlyList<TagMapping> snap, _) = store.GetSnapshot();
        TagMapping stored = snap.First(m => m.ItemId == "D100");
        Assert.Equal(string.Empty, stored.PlcGroup);
        Assert.Equal(0, stored.PollRateMs);
    }

    [Fact]
    public void TryUpdate_AssigningGroup_KeepsNumericField_AsStored()
    {
        MappingStore store = CreateStore();
        store.SetAll(new[] { Tag("D100") });

        TagMapping edited = Tag("D100", plcGroup: "Fast", pollRateMs: 0);
        Assert.True(store.TryUpdate(edited, out _));
        (IReadOnlyList<TagMapping> snap, _) = store.GetSnapshot();
        Assert.Equal("Fast", snap.First(m => m.ItemId == "D100").PlcGroup);
    }
}
