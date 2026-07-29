using OpcBridge.Client;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class HmiClientMergeTests
{
    [Fact]
    public void ApplyDelta_UpdatesMatchingTagValue()
    {
        var cache = new HmiTagCache();
        cache.ReplaceAll(
        [
            new HmiTagDto
            {
                SourceId = "default",
                ItemId = "Random.Int1",
                DisplayName = "Int1",
                Value = 1,
                Writeable = true
            }
        ]);

        cache.ApplyDeltas(
        [
            new HmiValueDelta
            {
                SourceId = "default",
                ItemId = "Random.Int1",
                Value = 42,
                TimestampUtc = DateTime.UtcNow,
                DaQuality = 192,
                IsGood = true
            }
        ]);

        HmiTagDto tag = cache.Tags.Single();
        Assert.Equal(42, Convert.ToInt32(tag.Value));
        Assert.True(tag.IsGood);
        Assert.Equal(192, tag.DaQuality);
    }

    [Fact]
    public void ApplyDelta_IgnoresUnknownTag()
    {
        var cache = new HmiTagCache();
        cache.ReplaceAll(
        [
            new HmiTagDto
            {
                SourceId = "default",
                ItemId = "Known",
                Value = 1
            }
        ]);

        cache.ApplyDeltas(
        [
            new HmiValueDelta
            {
                SourceId = "default",
                ItemId = "Unknown",
                Value = 99,
                TimestampUtc = DateTime.UtcNow,
                DaQuality = 192,
                IsGood = true
            }
        ]);

        HmiTagDto tag = cache.Tags.Single();
        Assert.Equal(1, Convert.ToInt32(tag.Value));
        Assert.Equal("Known", tag.ItemId);
    }

    [Fact]
    public void ReplaceAll_ReplacesSnapshot()
    {
        var cache = new HmiTagCache();
        cache.ReplaceAll(
        [
            new HmiTagDto { SourceId = "a", ItemId = "1", Value = 1 }
        ]);
        cache.ReplaceAll(
        [
            new HmiTagDto { SourceId = "b", ItemId = "2", Value = 2 }
        ]);

        Assert.Single(cache.Tags);
        Assert.Equal("b", cache.Tags.Single().SourceId);
        Assert.Equal(2, Convert.ToInt32(cache.Tags.Single().Value));
    }
}
