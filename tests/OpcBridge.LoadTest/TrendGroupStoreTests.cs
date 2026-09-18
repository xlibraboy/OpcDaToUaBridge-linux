using OpcBridge.Hmi.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class TrendGroupStoreTests : IDisposable
{
    private readonly string dir_;
    private readonly string file_;

    public TrendGroupStoreTests()
    {
        dir_ = Path.Combine(Path.GetTempPath(), "OpcBridge.TrendGroupStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir_);
        file_ = Path.Combine(dir_, "hmi-trendgroups.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(dir_))
            {
                Directory.Delete(dir_, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void SaveAndLoad_RoundTripsGroupsAndPenState()
    {
        var definition = new TrendGroupDefinition
        {
            Id = "boiler",
            Name = "Boiler",
            LayoutMode = TrendGroupLayouts.Stacked,
            YAxisMode = TrendGroupAxisModes.Percent,
            Pens =
            {
                new TrendGroupPenDefinition
                {
                    BridgeId = "default",
                    SourceId = "ua-sim",
                    DaItemId = "Tank1.Level",
                    Color = "#F48FB1",
                    Visible = false,
                    AxisAutoRange = false,
                    RangeMin = 0,
                    RangeMax = 100
                },
                new TrendGroupPenDefinition
                {
                    BridgeId = "line2",
                    SourceId = "melsec",
                    DaItemId = "Pump1.Run"
                }
            }
        };

        TrendGroupStore.Save(file_, new[] { definition });
        List<TrendGroupDefinition> loaded = TrendGroupStore.Load(file_);

        TrendGroupDefinition group = Assert.Single(loaded);
        Assert.Equal("boiler", group.Id);
        Assert.Equal("Boiler", group.Name);
        Assert.Equal("Stacked", group.LayoutMode);
        Assert.Equal("Percent", group.YAxisMode);

        Assert.Equal(2, group.Pens.Count);
        TrendGroupPenDefinition pen = group.Pens[0];
        Assert.Equal(TagBindingKey.Create("default", "ua-sim", "Tank1.Level"), pen.Key);
        Assert.Equal("#F48FB1", pen.Color);
        Assert.False(pen.Visible);
        Assert.False(pen.AxisAutoRange);
        Assert.Equal(0, pen.RangeMin);
        Assert.Equal(100, pen.RangeMax);

        // The second pen keeps its defaults; unset bounds are not written to disk at all.
        Assert.True(group.Pens[1].Visible);
        Assert.True(group.Pens[1].AxisAutoRange);
        Assert.Null(group.Pens[1].RangeMin);
        Assert.Null(group.Pens[1].RangeMax);
        Assert.DoesNotContain("RangeMin\": null", File.ReadAllText(file_), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyList()
    {
        Assert.Empty(TrendGroupStore.Load(Path.Combine(dir_, "missing.json")));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyList()
    {
        File.WriteAllText(file_, "{ not json at all");
        Assert.Empty(TrendGroupStore.Load(file_));
    }

    [Fact]
    public void Load_RepairsIdsNamesAndModes()
    {
        File.WriteAllText(
            file_,
            """
            [
              { "id": "", "name": "  ", "layoutMode": "sideways", "yAxisMode": "upside-down", "pens": [] },
              { "id": "same", "name": "  Trimmed  ", "pens": [] },
              { "id": "same", "name": "Boiler", "pens": [] }
            ]
            """);

        List<TrendGroupDefinition> loaded = TrendGroupStore.Load(file_);

        Assert.Equal(3, loaded.Count);
        Assert.Equal("Trend group 1", loaded[0].Name);
        Assert.Equal(TrendGroupLayouts.Mixed, loaded[0].LayoutMode);
        Assert.Equal(TrendGroupAxisModes.Shared, loaded[0].YAxisMode);
        Assert.False(string.IsNullOrWhiteSpace(loaded[0].Id));

        Assert.Equal("Trimmed", loaded[1].Name);
        Assert.NotEqual(loaded[1].Id, loaded[2].Id);
        Assert.All(loaded, group => Assert.False(string.IsNullOrWhiteSpace(group.Id)));
    }

    [Fact]
    public void Load_DropsPensWithBlankKeyPartsAndDuplicates()
    {
        File.WriteAllText(
            file_,
            """
            [
              {
                "id": "g1",
                "name": "Boiler",
                "pens": [
                  { "bridgeId": "default", "sourceId": "src", "daItemId": "Tank1.Level" },
                  { "bridgeId": "default", "sourceId": "src", "daItemId": "" },
                  { "bridgeId": "", "sourceId": "src", "daItemId": "Pump1.Run" },
                  { "bridgeId": "DEFAULT", "sourceId": "SRC", "daItemId": "tank1.level" }
                ]
              }
            ]
            """);

        TrendGroupDefinition group = Assert.Single(TrendGroupStore.Load(file_));

        // The duplicate (same key, different case) and the two key-less pens are gone.
        TrendGroupPenDefinition pen = Assert.Single(group.Pens);
        Assert.Equal("Tank1.Level", pen.DaItemId);
    }

    [Fact]
    public void Load_NormalizesColorsAndRanges()
    {
        File.WriteAllText(
            file_,
            """
            [
              {
                "id": "g1",
                "name": "Boiler",
                "pens": [
                  { "bridgeId": "b", "sourceId": "s", "daItemId": "a", "color": "red", "rangeMin": "NaN", "rangeMax": 50 },
                  { "bridgeId": "b", "sourceId": "s", "daItemId": "b", "color": "#a5d6a7" }
                ]
              }
            ]
            """);

        TrendGroupDefinition group = Assert.Single(TrendGroupStore.Load(file_));

        // An unusable colour falls back to the palette, a non-finite bound is dropped.
        Assert.Equal(string.Empty, group.Pens[0].Color);
        Assert.Null(group.Pens[0].RangeMin);
        Assert.Equal(50, group.Pens[0].RangeMax);
        Assert.Equal("#A5D6A7", group.Pens[1].Color);
    }

    [Fact]
    public void DefaultPath_SitsNextToTheClientConfig()
    {
        string configPath = Path.Combine(dir_, "hmi-config.json");
        Assert.Equal(Path.Combine(dir_, "hmi-trendgroups.json"), TrendGroupStore.DefaultPath(configPath));
    }
}
