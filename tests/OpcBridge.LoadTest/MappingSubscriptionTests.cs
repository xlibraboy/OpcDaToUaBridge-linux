using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// MappingStore subscription support: Subscription is trimmed on every
/// normalization path and survives persistence; ReassignSubscription moves a
/// deleted named subscription's tags back onto the source default (spec §6).
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class MappingSubscriptionTests
{
    // MappingStore persists to AppContext.BaseDirectory/mappings.json (the test
    // bin folder) and prefers disk over seeded options — clean it per test.
    public MappingSubscriptionTests()
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

    private static TagMapping Tag(string sourceId, string itemId, string? subscription = null) => new()
    {
        SourceId = sourceId,
        ItemId = itemId,
        UaNodeId = "ns=2;s=" + itemId,
        DisplayName = itemId,
        Subscription = subscription ?? string.Empty
    };

    private static TagMapping Single(IReadOnlyList<TagMapping> mappings, string itemId) =>
        mappings.Single(m => m.ItemId == itemId);

    [Fact]
    public void Store_TrimsSubscription_OnAddAndUpdate()
    {
        MappingStore store = NewStore();

        store.Add([Tag("ua-a", "a1", " Fast ")]);
        (IReadOnlyList<TagMapping> snap, _) = store.GetSnapshot();
        Assert.Equal("Fast", Single(snap, "a1").Subscription);

        Assert.True(store.TryUpdate(Tag("ua-a", "a1", "  Slow  "), out _));
        (IReadOnlyList<TagMapping> snap2, _) = store.GetSnapshot();
        Assert.Equal("Slow", Single(snap2, "a1").Subscription);
    }

    [Fact]
    public void Store_RoundTripsSubscription_ToDisk()
    {
        MappingStore store = NewStore();
        store.Add([Tag("ua-a", "a1", " Fast "), Tag("ua-a", "a2", "MysterySub")]);

        // Fresh instance over the same backing file: persisted disk state wins
        // over the (empty) seed, so both values must survive the reload.
        var reloaded = new MappingStore(Options.Create(new BridgeOptions()));
        (IReadOnlyList<TagMapping> snap, _) = reloaded.GetSnapshot();
        Assert.Equal("Fast", Single(snap, "a1").Subscription);
        // Unknown names are not validated away — stored verbatim.
        Assert.Equal("MysterySub", Single(snap, "a2").Subscription);
    }

    [Fact]
    public void ReassignSubscription_MovesOnlyMatchingSource_CaseInsensitive_ReturnsCount()
    {
        MappingStore store = NewStore(
            Tag("ua-a", "a1", "Fast"),
            Tag("ua-a", "a2", "fast"),
            Tag("ua-a", "a3"),
            Tag("ua-b", "b1", "FAST"));

        int moved = store.ReassignSubscription("ua-a", "FAST");

        Assert.Equal(2, moved);
        (IReadOnlyList<TagMapping> snap, _) = store.GetSnapshot();
        Assert.Equal(string.Empty, Single(snap, "a1").Subscription);
        Assert.Equal(string.Empty, Single(snap, "a2").Subscription);
        Assert.Equal(string.Empty, Single(snap, "a3").Subscription); // already default, untouched
        Assert.Equal("FAST", Single(snap, "b1").Subscription);       // other source untouched
    }

    [Fact]
    public void ReassignSubscription_NoOp_ForBlankOrUnknownName()
    {
        MappingStore store = NewStore(
            Tag("ua-a", "a1", "Fast"),
            Tag("ua-a", "a2"));

        Assert.Equal(0, store.ReassignSubscription("ua-a", ""));
        Assert.Equal(0, store.ReassignSubscription("ua-a", "   "));
        Assert.Equal(0, store.ReassignSubscription("ua-a", "Missing"));
        Assert.Equal(0, store.ReassignSubscription("", "Fast"));
        Assert.Equal(0, store.ReassignSubscription("ua-zz", "Fast")); // unknown source

        (IReadOnlyList<TagMapping> snap, _) = store.GetSnapshot();
        Assert.Equal("Fast", Single(snap, "a1").Subscription);
        Assert.Equal(string.Empty, Single(snap, "a2").Subscription);
    }

    [Fact]
    public void ReassignSubscription_PersistsAndBroadcasts_OncePerCall()
    {
        MappingStore store = NewStore(
            Tag("ua-a", "a1", "Fast"),
            Tag("ua-a", "a2", "fast"),
            Tag("ua-a", "a3", "Slow"),
            Tag("ua-a", "a4"));

        long baseVersion = store.Version;
        int broadcasts = 0;
        store.Changed += _ => broadcasts++;

        int moved = store.ReassignSubscription("ua-a", "FAST");

        // Two matched tags move in ONE batched write: exactly one version bump
        // and one Changed broadcast for the whole call (per-tag TryUpdate would
        // have produced two of each).
        Assert.Equal(2, moved);
        Assert.Equal(baseVersion + 1, store.Version);
        Assert.Equal(1, broadcasts);

        (IReadOnlyList<TagMapping> snap, _) = store.GetSnapshot();
        Assert.Equal(string.Empty, Single(snap, "a1").Subscription);
        Assert.Equal(string.Empty, Single(snap, "a2").Subscription);
        Assert.Equal("Slow", Single(snap, "a3").Subscription);      // other subscription untouched
        Assert.Equal(string.Empty, Single(snap, "a4").Subscription); // already default, untouched

        // No-match calls stay fully silent: no persist, no broadcast.
        Assert.Equal(0, store.ReassignSubscription("ua-zz", "Fast"));
        Assert.Equal(baseVersion + 1, store.Version);
        Assert.Equal(1, broadcasts);
    }
}
