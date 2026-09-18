using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;
using OpcBridge.Hmi.ViewModels;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class TrendGroupPenStateTests
{
    [Fact]
    public void Apply_RestoresVisibilityAxisAndTypedRange()
    {
        var saved = new TrendGroupPenDefinition
        {
            BridgeId = "default",
            SourceId = "ua-sim",
            DaItemId = "Tank1.Level",
            Color = "#F48FB1",
            Visible = false,
            AxisAutoRange = false,
            RangeMin = 0,
            RangeMax = 100
        };
        using var api = new BridgeApiClient();
        var pen = new TrendPenViewModel(saved.Key, api, displayName: "Level of Tank 1", color: saved.Color);

        TrendGroupPenState.Apply(pen, saved);

        Assert.False(pen.IsVisible);
        Assert.False(pen.AxisAutoRange);
        Assert.True(pen.HasCustomAxis);
        Assert.Equal(0, pen.CustomRangeMin);
        Assert.Equal(100, pen.CustomRangeMax);
    }

    [Fact]
    public void Apply_DropsATypedRangeTheSavedGroupDoesNotHave()
    {
        var saved = new TrendGroupPenDefinition { BridgeId = "default", SourceId = "ua-sim", DaItemId = "Tank1.Level" };
        using var api = new BridgeApiClient();
        var pen = new TrendPenViewModel(saved.Key, api);
        pen.SetCustomRange(1, 2);

        TrendGroupPenState.Apply(pen, saved);

        Assert.False(pen.HasCustomAxis);
        Assert.Null(pen.CustomRangeMin);
        Assert.Null(pen.CustomRangeMax);
    }

    [Fact]
    public void Capture_WritesTheOperatorEditsBack()
    {
        TagBindingKey key = TagBindingKey.Create("default", "ua-sim", "Tank1.Level");
        using var api = new BridgeApiClient();
        var pen = new TrendPenViewModel(key, api, color: "#4FC3F7")
        {
            IsVisible = false,
            AxisAutoRange = false
        };
        pen.SetCustomRange(2.5, 7.5);

        var saved = new TrendGroupPenDefinition { BridgeId = "default", SourceId = "ua-sim", DaItemId = "Tank1.Level" };
        TrendGroupPenState.Capture(pen, saved);

        Assert.Equal("#4FC3F7", saved.Color);
        Assert.False(saved.Visible);
        Assert.False(saved.AxisAutoRange);
        Assert.Equal(2.5, saved.RangeMin);
        Assert.Equal(7.5, saved.RangeMax);
    }

    [Fact]
    public void Pen_KnowsWhenItsColorWasSaved()
    {
        TagBindingKey key = TagBindingKey.Create("default", "ua-sim", "Tank1.Level");
        using var api = new BridgeApiClient();

        Assert.True(new TrendPenViewModel(key, api, color: "#F48FB1").HasExplicitColor);
        Assert.False(new TrendPenViewModel(key, api).HasExplicitColor);
    }

    [Fact]
    public async Task GroupTrend_KeepsSavedColorsAndFillsTheRestFromThePalette()
    {
        using var api = new BridgeApiClient();
        var saved = new TrendGroupPenDefinition
        {
            BridgeId = "default",
            SourceId = "ua-sim",
            DaItemId = "Tank1.Level",
            Color = "#F48FB1"
        };
        var restored = new TrendPenViewModel(saved.Key, api, color: saved.Color);
        var palettePen = new TrendPenViewModel(TagBindingKey.Create("default", "ua-sim", "Pump1.Run"), api);
        TrendGroupPenState.Apply(restored, saved);

        var group = new TrendGroupViewModel(new[] { restored, palettePen }, "Boiler");
        try
        {
            Assert.Equal("#F48FB1", group.Pens[0].Color);
            Assert.Equal(TrendSeriesPalette.ColorFor(1), group.Pens[1].Color);
            Assert.Equal("Boiler · 2 tags", group.Title);
        }
        finally
        {
            await group.DisposeAsync();
        }
    }
}
