using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.ViewModels;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class TrendGroupItemViewModelTests
{
    [Fact]
    public void RefreshRows_MarksTagsTheBridgeNoLongerHas()
    {
        var definition = new TrendGroupDefinition { Id = "g1", Name = "Boiler" };
        definition.Pens.Add(Pen("Tank1.Level"));
        definition.Pens.Add(Pen("Gone.Tag"));
        var item = new TrendGroupItemViewModel(definition);
        var cached = new MultiBridgeTagEntry
        {
            Key = TagBindingKey.Create("default", "ua-sim", "Tank1.Level"),
            DisplayName = "Level of Tank 1",
            SourceName = "ua-sim"
        };

        item.RefreshRows(key => key.DaItemId == "Tank1.Level" ? cached : null);

        Assert.Equal(2, item.Tags.Count);
        Assert.Equal("Level of Tank 1", item.Tags[0].DisplayName);
        Assert.Equal("ua-sim", item.Tags[0].SourceName);
        Assert.False(item.Tags[0].HasMarker);

        // A tag that is gone falls back to its item id but stays in the group (and removable).
        Assert.Equal("Gone.Tag", item.Tags[1].DisplayName);
        Assert.Equal("not on bridge", item.Tags[1].Marker);
        Assert.True(item.Tags[1].Enabled);

        Assert.Equal("2 tags · 1 not on bridge", item.Subtitle);
    }

    [Fact]
    public void AddTag_IgnoresTagsAlreadyInTheGroup()
    {
        var item = new TrendGroupItemViewModel(new TrendGroupDefinition { Id = "g1", Name = "Boiler" });

        Assert.True(item.AddTag(TagBindingKey.Create("default", "ua-sim", "Tank1.Level")));
        Assert.False(item.AddTag(TagBindingKey.Create("DEFAULT", "UA-SIM", "tank1.level")));
        Assert.True(item.AddTag(TagBindingKey.Create("default", "ua-sim", "Pump1.Run")));

        Assert.Equal(2, item.Definition.Pens.Count);

        item.RefreshRows(_ => null);
        Assert.Equal(2, item.Tags.Count);
    }

    [Fact]
    public void RemoveTag_DropsTheMember()
    {
        var item = new TrendGroupItemViewModel(new TrendGroupDefinition { Id = "g1", Name = "Boiler" });
        TagBindingKey key = TagBindingKey.Create("default", "ua-sim", "Tank1.Level");
        item.AddTag(key);

        Assert.True(item.RemoveTag(key));
        Assert.False(item.RemoveTag(key));
        Assert.Empty(item.Definition.Pens);
    }

    [Fact]
    public void Name_WritesThroughToTheDefinition()
    {
        var item = new TrendGroupItemViewModel(new TrendGroupDefinition { Id = "g1", Name = "Boiler" });

        item.Name = "Feedwater";

        Assert.Equal("Feedwater", item.Definition.Name);
    }

    private static TrendGroupPenDefinition Pen(string itemId) => new()
    {
        BridgeId = "default",
        SourceId = "ua-sim",
        DaItemId = itemId
    };
}
