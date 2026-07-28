using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class MultiBridgeTagCacheTests
{
    [Fact]
    public void ReplaceBridge_ScopesByBridgeId()
    {
        var cache = new MultiBridgeTagCache();
        cache.ReplaceBridge("line1",
        [
            new HmiTagDto { SourceId = "default", DaItemId = "A", DisplayName = "A1", Value = 1, Writeable = true }
        ]);
        cache.ReplaceBridge("line2",
        [
            new HmiTagDto { SourceId = "default", DaItemId = "A", DisplayName = "A2", Value = 2 }
        ]);

        Assert.Equal(2, cache.Tags.Count);
        Assert.True(cache.TryGet(TagBindingKey.Create("line1", "default", "A"), out MultiBridgeTagEntry? t1));
        Assert.True(cache.TryGet(TagBindingKey.Create("line2", "default", "A"), out MultiBridgeTagEntry? t2));
        Assert.Equal(1, Assert.IsType<int>(Convert.ToInt32(t1!.Value)));
        Assert.Equal(2, Assert.IsType<int>(Convert.ToInt32(t2!.Value)));
        Assert.Equal("A1", t1.DisplayName);
        Assert.Equal("A2", t2.DisplayName);
    }

    [Fact]
    public void ReplaceBridge_ReplacesOnlyThatBridge()
    {
        var cache = new MultiBridgeTagCache();
        cache.ReplaceBridge("line1",
        [
            new HmiTagDto { SourceId = "default", DaItemId = "A", Value = 1 },
            new HmiTagDto { SourceId = "default", DaItemId = "B", Value = 2 }
        ]);
        cache.ReplaceBridge("line2",
        [
            new HmiTagDto { SourceId = "default", DaItemId = "C", Value = 3 }
        ]);

        cache.ReplaceBridge("line1",
        [
            new HmiTagDto { SourceId = "default", DaItemId = "A", Value = 10 }
        ]);

        Assert.Equal(2, cache.Tags.Count);
        Assert.True(cache.TryGet(TagBindingKey.Create("line1", "default", "A"), out _));
        Assert.False(cache.TryGet(TagBindingKey.Create("line1", "default", "B"), out _));
        Assert.True(cache.TryGet(TagBindingKey.Create("line2", "default", "C"), out _));
    }

    [Fact]
    public void ApplyDeltas_UpdatesMatchingBridgeOnly()
    {
        var cache = new MultiBridgeTagCache();
        cache.ReplaceBridge("line1",
        [
            new HmiTagDto { SourceId = "default", DaItemId = "A", Value = 1, IsGood = true }
        ]);
        cache.ReplaceBridge("line2",
        [
            new HmiTagDto { SourceId = "default", DaItemId = "A", Value = 9, IsGood = true }
        ]);

        cache.ApplyDeltas("line1",
        [
            new HmiValueDelta
            {
                SourceId = "default",
                DaItemId = "A",
                Value = 42,
                IsGood = false,
                DaQuality = 0,
                TimestampUtc = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc)
            }
        ]);

        Assert.True(cache.TryGet(TagBindingKey.Create("line1", "default", "A"), out MultiBridgeTagEntry? t1));
        Assert.True(cache.TryGet(TagBindingKey.Create("line2", "default", "A"), out MultiBridgeTagEntry? t2));
        Assert.Equal(42, Convert.ToInt32(t1!.Value));
        Assert.False(t1.IsGood);
        Assert.Equal(9, Convert.ToInt32(t2!.Value));
        Assert.True(t2.IsGood);
    }

    [Fact]
    public void ApplyDeltas_IgnoresUnknownTags()
    {
        var cache = new MultiBridgeTagCache();
        cache.ReplaceBridge("line1",
        [
            new HmiTagDto { SourceId = "default", DaItemId = "A", Value = 1 }
        ]);
        cache.ApplyDeltas("line1",
        [
            new HmiValueDelta { SourceId = "default", DaItemId = "missing", Value = 99 }
        ]);
        Assert.Single(cache.Tags);
        Assert.True(cache.TryGet(TagBindingKey.Create("line1", "default", "A"), out MultiBridgeTagEntry? t));
        Assert.Equal(1, Convert.ToInt32(t!.Value));
    }

    [Fact]
    public void TagBindingKey_CaseInsensitiveEquality()
    {
        var a = TagBindingKey.Create("Line1", "Default", "Tank.Level");
        var b = TagBindingKey.Create("line1", "default", "tank.level");
        Assert.True(TagBindingKeyComparer.Instance.Equals(a, b));
        Assert.Equal(
            TagBindingKeyComparer.Instance.GetHashCode(a),
            TagBindingKeyComparer.Instance.GetHashCode(b));
    }
}
