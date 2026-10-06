using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Designer.Services;
using OpcBridge.Hmi.Designer.ViewModels;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Designer's source list: configured sources win, tag-derived sources fill in when the
/// list is unavailable (signed out / bridge older than the endpoint).
/// </summary>
public sealed class DesignerSourceCatalogTests
{
    private static MultiBridgeTagEntry Entry(string sourceId, string sourceName, string sourceType, string itemId) => new()
    {
        Key = TagBindingKey.Create("default", sourceId, itemId),
        SourceName = sourceName,
        SourceType = sourceType,
        DisplayName = itemId
    };

    private static BridgeSourceInfo Configured(string sourceId, string displayName, string type = "OpcUa") =>
        new(sourceId, displayName, type, "opc.tcp://host:4840/S", "Connected");

    [Fact]
    public void Build_DerivesSourcesFromTags_WhenNothingIsConfigured()
    {
        IReadOnlyList<BridgeSourceInfo> sources = DesignerSourceCatalog.Build(
            new[] { Entry("s1", "Line 1", "OpcDa", "Tank.Level"), Entry("s1", "Line 1", "OpcDa", "Tank.Flow") },
            null);

        BridgeSourceInfo source = Assert.Single(sources);
        Assert.Equal("s1", source.SourceId);
        Assert.Equal("Line 1", source.DisplayName);
        Assert.Equal("OpcDa", source.SourceType);
        Assert.Equal("DA", source.TypeLabel);
        Assert.Null(source.ConnectionState);
    }

    [Fact]
    public void Build_ConfiguredSourcesWin_AndTagsAddTheMissingOnes()
    {
        IReadOnlyList<BridgeSourceInfo> sources = DesignerSourceCatalog.Build(
            new[] { Entry("s1", "Old Name", "OpcDa", "Tank.Level"), Entry("s2", "Line 2", "OpcDa", "Flow") },
            new[] { Configured("s1", "Line 1", "OpcUa") });

        Assert.Equal(2, sources.Count);
        BridgeSourceInfo s1 = sources.Single(source => source.SourceId == "s1");
        Assert.Equal("Line 1", s1.DisplayName);
        Assert.Equal("OpcUa", s1.SourceType);
        Assert.Equal("Connected", s1.ConnectionState);

        BridgeSourceInfo s2 = sources.Single(source => source.SourceId == "s2");
        Assert.Equal("Line 2", s2.DisplayName);
        Assert.Equal("OpcDa", s2.SourceType);
    }

    [Fact]
    public void Build_SortsByDisplayName_ThenSourceId()
    {
        IReadOnlyList<BridgeSourceInfo> sources = DesignerSourceCatalog.Build(
            Array.Empty<MultiBridgeTagEntry>(),
            new[] { Configured("b", "Zulu"), Configured("a", "Alpha"), Configured("c", "Alpha") });

        Assert.Equal(new[] { "a", "c", "b" }, sources.Select(source => source.SourceId).ToArray());
    }
}
