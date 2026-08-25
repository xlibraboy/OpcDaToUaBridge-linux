using System.Text.Json;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.ViewModels;
using OpcBridge.Hmi.ViewModels.Widgets;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DisplaySurfaceViewModelTests
{
    [Fact]
    public void Load_CreatesKnownWidgets_AndPlaceholderForUnknown()
    {
        var cache = new MultiBridgeTagCache();
        cache.ReplaceBridge("line1",
        [
            new HmiTagDto
            {
                SourceId = "default",
                ItemId = "Tank.Level",
                DisplayName = "Tank",
                Value = 12.5,
                IsGood = true,
                DataType = "Double"
            }
        ]);

        var surface = new DisplaySurfaceViewModel(
            cache,
            _ => { },
            (_, _) => Task.FromResult((true, (string?)null)));

        surface.Load(new DisplayDocumentDto
        {
            SchemaVersion = 1,
            Id = "plant",
            Name = "Plant",
            Width = 800,
            Height = 600,
            Widgets =
            [
                new DisplayWidgetDto
                {
                    Id = "l1",
                    Type = "label",
                    X = 0,
                    Y = 0,
                    W = 100,
                    H = 20,
                    Props = new Dictionary<string, JsonElement>
                    {
                        ["text"] = JsonSerializer.SerializeToElement("Hello")
                    }
                },
                new DisplayWidgetDto
                {
                    Id = "n1",
                    Type = "numeric",
                    X = 10,
                    Y = 30,
                    W = 120,
                    H = 40,
                    Binding = new TagBindingDto
                    {
                        BridgeId = "line1",
                        SourceId = "default",
                        DaItemId = "Tank.Level"
                    }
                },
                new DisplayWidgetDto
                {
                    Id = "x1",
                    Type = "fancyGauge",
                    X = 0,
                    Y = 80,
                    W = 50,
                    H = 50
                }
            ]
        });

        Assert.True(surface.HasDocument);
        Assert.Equal(3, surface.Widgets.Count);
        Assert.IsType<LabelWidgetViewModel>(surface.Widgets[0]);
        Assert.IsType<NumericWidgetViewModel>(surface.Widgets[1]);
        Assert.IsType<UnsupportedWidgetViewModel>(surface.Widgets[2]);
        Assert.False(surface.Widgets[1].IsUnbound);
        Assert.Contains("12.5", surface.Widgets[1].ValueText, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_UnknownBridge_MarksUnbound()
    {
        var cache = new MultiBridgeTagCache();
        var surface = new DisplaySurfaceViewModel(cache, _ => { }, (_, _) => Task.FromResult((true, (string?)null)));
        surface.Load(new DisplayDocumentDto
        {
            SchemaVersion = 1,
            Id = "p",
            Name = "P",
            Width = 100,
            Height = 100,
            Widgets =
            [
                new DisplayWidgetDto
                {
                    Id = "n1",
                    Type = "numeric",
                    W = 40,
                    H = 20,
                    Binding = new TagBindingDto
                    {
                        BridgeId = "missing",
                        SourceId = "default",
                        DaItemId = "A"
                    }
                }
            ]
        });

        Assert.True(surface.Widgets[0].IsUnbound);
        Assert.Equal("Bridge not configured", surface.Widgets[0].StatusText);
    }

    [Fact]
    public void Load_BadSchema_SetsStatus()
    {
        var surface = new DisplaySurfaceViewModel(new MultiBridgeTagCache(), _ => { }, (_, _) => Task.FromResult((true, (string?)null)));
        surface.Load(new DisplayDocumentDto
        {
            SchemaVersion = 9,
            Id = "p",
            Name = "P",
            Width = 100,
            Height = 100
        });
        Assert.False(surface.HasDocument);
        Assert.Contains("schemaVersion", surface.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }
}
