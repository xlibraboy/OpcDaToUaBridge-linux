using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.ViewModels;
using OpcBridge.Hmi.ViewModels.Widgets;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DisplaySurfaceMoveTests
{
    [Fact]
    public void MoveWidgetTo_ClampsAndUpdatesBounds()
    {
        var surface = new DisplaySurfaceViewModel(new MultiBridgeTagCache(), _ => { }, (_, _) => Task.FromResult((true, (string?)null)));
        surface.ApplyDesignMode(true);
        surface.Load(new DisplayDocumentDto
        {
            SchemaVersion = 1,
            Id = "p",
            Name = "P",
            Width = 200,
            Height = 100,
            Widgets =
            [
                new DisplayWidgetDto
                {
                    Id = "w1",
                    Type = "label",
                    X = 10,
                    Y = 10,
                    W = 40,
                    H = 20,
                    Props = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["text"] = System.Text.Json.JsonSerializer.SerializeToElement("A")
                    }
                }
            ]
        });

        WidgetViewModelBase widget = Assert.Single(surface.Widgets);
        surface.MoveWidgetTo(widget, 500, 500);
        Assert.Equal(160, widget.X); // 200 - 40
        Assert.Equal(80, widget.Y);  // 100 - 20

        surface.MoveWidgetTo(widget, -20, -20);
        Assert.Equal(0, widget.X);
        Assert.Equal(0, widget.Y);
    }

    [Fact]
    public void ExportWidgetDtos_IncludesMovedBounds()
    {
        var surface = new DisplaySurfaceViewModel(new MultiBridgeTagCache(), _ => { }, (_, _) => Task.FromResult((true, (string?)null)));
        surface.Load(new DisplayDocumentDto
        {
            SchemaVersion = 1,
            Id = "p",
            Name = "P",
            Width = 400,
            Height = 300,
            Widgets =
            [
                new DisplayWidgetDto
                {
                    Id = "n1",
                    Type = "numeric",
                    X = 5,
                    Y = 6,
                    W = 50,
                    H = 30,
                    Binding = new TagBindingDto
                    {
                        BridgeId = "line1",
                        SourceId = "default",
                        DaItemId = "A"
                    }
                }
            ]
        });

        WidgetViewModelBase widget = Assert.Single(surface.Widgets);
        surface.MoveWidgetTo(widget, 40, 50);
        IReadOnlyList<DisplayWidgetDto> exported = surface.ExportWidgetDtos();
        Assert.Single(exported);
        Assert.Equal(40, exported[0].X);
        Assert.Equal(50, exported[0].Y);
        TagBindingDto? binding = exported[0].Binding;
        Assert.NotNull(binding);
        Assert.Equal("line1", binding!.BridgeId);
        Assert.Equal("A", binding.DaItemId);
    }

    [Fact]
    public void BuildConfigFromUi_ParsesExtraBridges()
    {
        // Exercise HmiClientConfig parsing path used by Runtime multi-bridge text box.
        var config = new HmiClientConfig
        {
            DisplayStoreUrl = "http://primary:8080",
            Bridges =
            [
                new HmiBridgeEndpoint { Id = "default", BaseUrl = "http://primary:8080", Enabled = true },
                new HmiBridgeEndpoint { Id = "line2", BaseUrl = "http://peer:8080", Enabled = true }
            ]
        };

        string path = Path.Combine(Path.GetTempPath(), "hmi-config-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            config.Save(path);
            HmiClientConfig loaded = HmiClientConfig.LoadOrDefault(path);
            Assert.Equal(2, loaded.Bridges.Count);
            Assert.Equal("line2", loaded.Bridges[1].Id);
            Assert.Equal("http://peer:8080", loaded.Bridges[1].BaseUrl);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
