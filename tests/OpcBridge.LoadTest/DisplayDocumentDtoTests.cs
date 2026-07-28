using System.Text.Json;
using OpcBridge.Client;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DisplayDocumentDtoTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    [Fact]
    public void DisplayDocument_RoundTripsJson()
    {
        var doc = new DisplayDocumentDto
        {
            SchemaVersion = 1,
            Id = "plant-overview",
            Name = "Plant Overview",
            Version = 3,
            UpdatedUtc = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc),
            Width = 1920,
            Height = 1080,
            Widgets =
            [
                new DisplayWidgetDto
                {
                    Id = "w1",
                    Type = "numeric",
                    X = 40,
                    Y = 80,
                    W = 160,
                    H = 48,
                    Z = 0,
                    Props = new Dictionary<string, JsonElement>
                    {
                        ["label"] = JsonSerializer.SerializeToElement("Tank Level"),
                        ["format"] = JsonSerializer.SerializeToElement("0.0")
                    },
                    Binding = new TagBindingDto
                    {
                        BridgeId = "line1",
                        SourceId = "default",
                        DaItemId = "Tank.Level"
                    }
                },
                new DisplayWidgetDto
                {
                    Id = "w2",
                    Type = "label",
                    X = 40,
                    Y = 40,
                    W = 200,
                    H = 32,
                    Props = new Dictionary<string, JsonElement>
                    {
                        ["text"] = JsonSerializer.SerializeToElement("Line 1")
                    },
                    Binding = null
                }
            ]
        };

        string json = JsonSerializer.Serialize(doc, JsonOptions);
        DisplayDocumentDto? back = JsonSerializer.Deserialize<DisplayDocumentDto>(json, JsonOptions);

        Assert.NotNull(back);
        Assert.Equal(1, back!.SchemaVersion);
        Assert.Equal("plant-overview", back.Id);
        Assert.Equal("Plant Overview", back.Name);
        Assert.Equal(3, back.Version);
        Assert.Equal(1920, back.Width);
        Assert.Equal(1080, back.Height);
        Assert.Equal(2, back.Widgets.Count);
        Assert.Equal("numeric", back.Widgets[0].Type);
        Assert.Equal("line1", back.Widgets[0].Binding!.BridgeId);
        Assert.Equal("default", back.Widgets[0].Binding.SourceId);
        Assert.Equal("Tank.Level", back.Widgets[0].Binding.DaItemId);
        Assert.Null(back.Widgets[1].Binding);
        Assert.True(back.Widgets[0].Props.ContainsKey("label"));
    }

    [Fact]
    public void DisplayListItem_RoundTripsJson()
    {
        var list = new DisplayListResponse
        {
            Items =
            [
                new DisplayListItemDto
                {
                    Id = "plant-overview",
                    Name = "Plant Overview",
                    Version = 3,
                    UpdatedUtc = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc),
                    WidgetCount = 12
                }
            ]
        };

        string json = JsonSerializer.Serialize(list, JsonOptions);
        DisplayListResponse? back = JsonSerializer.Deserialize<DisplayListResponse>(json, JsonOptions);
        Assert.NotNull(back);
        Assert.Single(back!.Items);
        Assert.Equal(12, back.Items[0].WidgetCount);
    }
}
