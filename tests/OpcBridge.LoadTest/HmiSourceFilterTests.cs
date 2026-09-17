using OpcBridge.Client;
using OpcBridge.Core;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.ViewModels;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Covers the HMI tag browser's per-source separation: the source selector entries and the
/// per-tag source labels.
/// </summary>
public sealed class HmiSourceFilterTests
{
    private static MultiBridgeTagEntry Tag(string bridgeId, string sourceId, string sourceName, string sourceType) => new()
    {
        Key = TagBindingKey.Create(bridgeId, sourceId, "t"),
        SourceName = sourceName,
        SourceType = sourceType
    };

    [Fact]
    public void Build_ListsAllSourcesFirstThenOneEntryPerSource()
    {
        IReadOnlyList<SourceFilterOption> options = SourceFilterOptions.Build(
        [
            Tag("default", "default", "Default Source", "OpcDa"),
            Tag("default", "ua-a", "Line A", "OpcUa"),
            Tag("default", "default", "Default Source", "OpcDa")
        ]);

        Assert.Equal(3, options.Count);
        Assert.True(options[0].IsAll);
        Assert.Equal("All sources", options[0].Name);

        Assert.Equal(new[] { "Default Source", "Line A" }, options.Skip(1).Select(o => o.Name));
        Assert.Equal(new[] { "DA", "UA" }, options.Skip(1).Select(o => o.TypeLabel));
    }

    [Fact]
    public void Build_DisambiguatesWithTheBridgeWhenSeveralBridgesAreConnected()
    {
        IReadOnlyList<SourceFilterOption> options = SourceFilterOptions.Build(
        [
            Tag("line1", "default", "Default Source", "OpcDa"),
            Tag("line2", "default", "Default Source", "OpcDa")
        ]);

        Assert.Equal(
            new[] { "Default Source · line1", "Default Source · line2" },
            options.Skip(1).Select(o => o.Name));
    }

    [Fact]
    public void Build_ScopedToOneBridge_ListsOnlyItsSourcesWithoutTheBridgeSuffix()
    {
        MultiBridgeTagEntry[] tags =
        [
            Tag("line1", "default", "Default Source", "OpcDa"),
            Tag("line2", "default", "Default Source", "OpcDa"),
            Tag("line2", "ua-a", "Line A", "OpcUa")
        ];

        IReadOnlyList<SourceFilterOption> options = SourceFilterOptions.Build(tags, "line2");

        Assert.Equal(new[] { "Default Source", "Line A" }, options.Skip(1).Select(o => o.Name));
        Assert.Equal(new[] { "line2", "line2" }, options.Skip(1).Select(o => o.BridgeId));
    }

    [Fact]
    public void BridgeFilterOptions_ListsAllBridgesFirstThenOneEntryPerBridge()
    {
        IReadOnlyList<BridgeFilterOption> options = BridgeFilterOptions.Build(
        [
            Tag("line2", "default", "Default Source", "OpcDa"),
            Tag("default", "default", "Default Source", "OpcDa"),
            Tag("line2", "ua-a", "Line A", "OpcUa")
        ]);

        Assert.Equal(new[] { "All bridges", "default", "line2" }, options.Select(o => o.Name));
        Assert.True(options[0].IsAll);
        Assert.True(options[0].Matches("anything"));
        Assert.True(options[1].Matches("DEFAULT"));
        Assert.False(options[1].Matches("line2"));
        Assert.False(options[1].Matches(null));
    }

    [Fact]
    public void Build_WithoutSourceName_FallsBackToTheSourceId()
    {
        IReadOnlyList<SourceFilterOption> options = SourceFilterOptions.Build(
        [
            new MultiBridgeTagEntry { Key = TagBindingKey.Create("default", "ua-a", "t") }
        ]);

        SourceFilterOption source = Assert.Single(options, o => !o.IsAll);
        Assert.Equal("ua-a", source.Name);
        Assert.False(source.HasTypeLabel);
    }

    [Fact]
    public void Matches_ComparesBridgeAndSourceCaseInsensitively()
    {
        IReadOnlyList<SourceFilterOption> options = SourceFilterOptions.Build(
        [
            Tag("Line1", "UA-A", "Line A", "OpcUa")
        ]);
        SourceFilterOption source = Assert.Single(options, o => !o.IsAll);
        SourceFilterOption all = options[0];

        Assert.True(source.Matches(TagBindingKey.Create("line1", "ua-a", "any")));
        Assert.False(source.Matches(TagBindingKey.Create("line1", "other", "any")));
        Assert.False(source.Matches(TagBindingKey.Create("other", "ua-a", "any")));
        Assert.True(all.Matches(TagBindingKey.Create("any", "any", "any")));
    }

    [Fact]
    public void SourceTypeLabels_MapEveryBridgeSourceType()
    {
        Assert.Equal("DA", SourceTypeLabels.ShortLabel(SourceTypes.OpcDa));
        Assert.Equal("UA", SourceTypeLabels.ShortLabel(SourceTypes.OpcUa));
        Assert.Equal("A3N", SourceTypeLabels.ShortLabel(SourceTypes.MelsecA3n));
        Assert.Equal("S7-200", SourceTypeLabels.ShortLabel(SourceTypes.S7200Ppi));
        Assert.Equal("MX", SourceTypeLabels.ShortLabel(SourceTypes.MxComponent));
        Assert.Equal(string.Empty, SourceTypeLabels.ShortLabel(null));
        Assert.Equal(string.Empty, SourceTypeLabels.ShortLabel("  "));
    }

    [Fact]
    public void TagItemViewModel_ShowsSourceNameWithTypeBadge()
    {
        TagItemViewModel tag = TagItemViewModel.FromDto("default", new HmiTagDto
        {
            SourceId = "ua-a",
            SourceName = "Line A",
            SourceType = SourceTypes.OpcUa,
            ItemId = "T1",
            DisplayName = "T1"
        });

        Assert.Equal("Line A", tag.SourceName);
        Assert.Equal("UA", tag.SourceTypeLabel);
        Assert.Equal("Line A (UA)", tag.SourceDisplay);
    }

    [Fact]
    public void TagItemViewModel_UnknownSourceType_ShowsTheNameAlone()
    {
        TagItemViewModel tag = TagItemViewModel.FromDto("default", new HmiTagDto
        {
            SourceId = "ua-a",
            SourceName = "Line A",
            ItemId = "T1",
            DisplayName = "T1"
        });

        Assert.Equal(string.Empty, tag.SourceTypeLabel);
        Assert.Equal("Line A", tag.SourceDisplay);
    }
}
