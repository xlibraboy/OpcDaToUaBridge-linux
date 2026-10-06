using System.Text.Json;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.ViewModels.Widgets;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// History samples arrive as CLR primitives or as JsonElement after wire deserialization;
/// Boolean samples map to the digital strip's 0/1 band instead of being dropped as
/// non-numeric (a Boolean tag's trend used to read empty).
/// </summary>
public sealed class TrendValueCoercionTests
{
    [Fact]
    public void TryToDouble_BooleanHistoryPoints_PlotAsZeroAndOne()
    {
        // The exact wire shape that broke: HmiTrendPoint.V is object?, so a boolean history
        // sample deserializes to JsonElement (True/False), not a CLR bool.
        HmiTrendResponse original = new()
        {
            SourceId = "default",
            ItemId = "Tank.HighLevel",
            FromUtc = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2026, 10, 6, 1, 0, 0, DateTimeKind.Utc),
            Points = new[]
            {
                new HmiTrendPoint
                {
                    T = new DateTime(2026, 10, 6, 0, 30, 0, DateTimeKind.Utc),
                    V = true,
                    Good = true
                },
                new HmiTrendPoint
                {
                    T = new DateTime(2026, 10, 6, 0, 45, 0, DateTimeKind.Utc),
                    V = false,
                    Good = true
                }
            }
        };

        string json = JsonSerializer.Serialize(original);
        HmiTrendResponse? back = JsonSerializer.Deserialize<HmiTrendResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(back);
        Assert.Equal(2, back!.Points.Count);
        Assert.True(TrendValueCoercion.TryToDouble(back.Points[0].V, out double on));
        Assert.Equal(1, on);
        Assert.True(TrendValueCoercion.TryToDouble(back.Points[1].V, out double off));
        Assert.Equal(0, off);
    }

    [Theory]
    [InlineData(true, 1.0)]
    [InlineData(false, 0.0)]
    public void TryToDouble_ClrBooleans_MapToZeroAndOne(object value, double expected)
    {
        AssertCoerced(value, expected);
    }

    [Fact]
    public void TryToDouble_ClrNumericKinds_KeepTheirValue()
    {
        AssertCoerced((byte)7, 7);
        AssertCoerced((sbyte)-7, -7);
        AssertCoerced((short)-300, -300);
        AssertCoerced((ushort)60000, 60000);
        AssertCoerced(-42, -42);
        AssertCoerced(42u, 42);
        AssertCoerced(1234567890123L, 1234567890123);
        AssertCoerced(42UL, 42);
        AssertCoerced(1.5f, 1.5);
        AssertCoerced(2.25, 2.25);
        AssertCoerced(12.5m, 12.5);
    }

    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData("-3", -3.0)]
    [InlineData(" 7 ", 7.0)]
    public void TryToDouble_NumericStrings_Parse(string value, double expected)
    {
        AssertCoerced(value, expected);
    }

    [Theory]
    [InlineData("true", 1.0)]
    [InlineData("false", 0.0)]
    [InlineData("1", 1.0)]
    [InlineData("12.5", 12.5)]
    [InlineData("\"3.5\"", 3.5)]
    public void TryToDouble_JsonValues_Coerce(string json, double expected)
    {
        AssertCoerced(JsonDocument.Parse(json).RootElement, expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("on")]
    public void TryToDouble_UnusableValues_AreDropped(object? value)
    {
        Assert.False(TrendValueCoercion.TryToDouble(value, out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"on\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void TryToDouble_UnusableJsonValues_AreDropped(string json)
    {
        Assert.False(TrendValueCoercion.TryToDouble(JsonDocument.Parse(json).RootElement, out _));
    }

    [Fact]
    public void BoolIndicatorWidget_TracksJsonElementBooleanValues()
    {
        // The widget's own coercion missed JsonElement — the shape the tag cache holds after
        // wire deserialization — so a bound lamp always rendered its OFF text.
        var cache = new MultiBridgeTagCache();
        cache.ReplaceBridge("line1",
        [
            new HmiTagDto
            {
                SourceId = "default",
                ItemId = "Tank.HighLevel",
                DisplayName = "High level",
                DataType = "Boolean",
                Value = JsonDocument.Parse("true").RootElement,
                IsGood = true
            }
        ]);

        var widget = new BoolIndicatorWidgetViewModel(
            new DisplayWidgetDto
            {
                Id = "b1",
                Type = DisplayWidgetTypes.BoolIndicator,
                W = 60,
                H = 24,
                Binding = new TagBindingDto
                {
                    BridgeId = "line1",
                    SourceId = "default",
                    DaItemId = "Tank.HighLevel"
                }
            },
            cache,
            _ => { });

        // The base constructor resolves the cached value before the derived one assigns its
        // OnText/OffText labels, so the label text settles on the live refresh (as it does on
        // every live batch in the app).
        Assert.True(widget.IsOn);

        widget.RefreshFromCache();
        Assert.True(widget.IsOn);
        Assert.Equal("ON", widget.ValueText);

        cache.ApplyDeltas("line1", new[]
        {
            new HmiValueDelta
            {
                SourceId = "default",
                ItemId = "Tank.HighLevel",
                Value = JsonDocument.Parse("false").RootElement,
                TimestampUtc = DateTime.UtcNow,
                IsGood = true
            }
        });
        widget.RefreshFromCache();

        Assert.False(widget.IsOn);
        Assert.Equal("OFF", widget.ValueText);
    }

    private static void AssertCoerced(object value, double expected)
    {
        Assert.True(TrendValueCoercion.TryToDouble(value, out double y));
        Assert.Equal(expected, y);
    }
}
