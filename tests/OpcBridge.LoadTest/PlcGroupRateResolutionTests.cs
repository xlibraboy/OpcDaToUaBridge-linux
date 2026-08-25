using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Effective-rate resolution precedence (spec §5): defined group wins over per-tag PollRateMs;
/// unknown group names fall back; legacy per-tag and default paths untouched; resolution happens
/// at QUERY time so a live resolver swap is visible without rebuilding the cache.
/// </summary>
public sealed class PlcGroupRateResolutionTests
{
    private static TagMapping Tag(string itemId, string plcGroup = "", int pollRateMs = 0)
        => new() { SourceId = "mx1", ItemId = itemId, UaNodeId = $"ns=2;s={itemId}", PlcGroup = plcGroup, PollRateMs = pollRateMs };

    private static readonly IReadOnlyList<PlcGroupSettings> Groups = new[]
    {
        new PlcGroupSettings("Fast", 250),
        new PlcGroupSettings("Slow", 5000)
    };

    private static BridgeWorker.SourceMappingCache Build(
        IReadOnlyList<TagMapping> mappings,
        Func<string, IReadOnlyList<PlcGroupSettings>>? resolver = null)
        => BridgeWorker.SourceMappingCache.Build(mappings, Array.Empty<OpcBridge.App.InterlinkRule>(), resolver ?? (_ => Groups));

    [Fact]
    public void DistinctRates_IncludesGroupRates_AndExcludesSupersededTagRates()
    {
        BridgeWorker.SourceMappingCache cache = Build(new[]
        {
            Tag("D100", "Fast", 9999),  // group wins -> 250 (9999 never appears)
            Tag("D101", "Slow"),        // -> 5000
            Tag("D102", pollRateMs: 1000), // legacy per-tag -> 1000
            Tag("M0")                   // default -> 2000
        });

        IReadOnlyList<int> rates = cache.GetDistinctRates("mx1", 2000);
        Assert.Equal(new[] { 250, 1000, 2000, 5000 }, rates.Order().ToArray());
    }

    [Fact]
    public void ByRate_GroupedTagsLandUnderTheirGroupRate_NotTheirNumericRate()
    {
        BridgeWorker.SourceMappingCache cache = Build(new[] { Tag("D100", "Fast", 9999) });

        Assert.Single(cache.GetSourceReadMappingsByRate("mx1", 250, 2000));
        Assert.Empty(cache.GetSourceReadMappingsByRate("mx1", 9999, 2000));
    }

    [Fact]
    public void UnknownGroupName_FallsBack_LikeUnassigned()
    {
        BridgeWorker.SourceMappingCache cache = Build(new[] { Tag("D100", "Ghost", 750) });
        Assert.Equal(new[] { 750 }, cache.GetDistinctRates("mx1", 2000).Order().ToArray());
        Assert.Single(cache.GetSourceReadMappingsByRate("mx1", 750, 2000));
    }

    [Fact]
    public void QueryTimeResolution_ResolverSwap_VisibleWithoutRebuild()
    {
        IReadOnlyList<PlcGroupSettings> initial = new[] { new PlcGroupSettings("Fast", 250) };
        IReadOnlyList<PlcGroupSettings> updated = new[] { new PlcGroupSettings("Fast", 400) };
        IReadOnlyList<PlcGroupSettings>? current = initial;

        BridgeWorker.SourceMappingCache cache = Build(new[] { Tag("D100", "Fast") }, _ => current!);

        Assert.Contains(250, cache.GetDistinctRates("mx1", 2000));
        current = updated;                          // settings snapshot moved underneath
        Assert.Contains(400, cache.GetDistinctRates("mx1", 2000)); // no rebuild needed
        Assert.DoesNotContain(250, cache.GetDistinctRates("mx1", 2000));
    }

    [Fact]
    public void OtherSources_Unaffected_ByMxGroups()
    {
        BridgeWorker.SourceMappingCache cache = Build(new[]
        {
            new TagMapping { SourceId = "da1", ItemId = "Item.A", UaNodeId = "x" },
            Tag("D100", "Fast")
        }, _ => Groups);

        Assert.Equal(new[] { 2000 }, cache.GetDistinctRates("da1", 2000));
    }
}
