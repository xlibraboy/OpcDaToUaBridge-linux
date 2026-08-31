using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Tests for the per-tag Decimals setting: null = passthrough, N = round
/// floating-point values to N digits after the comma, integers untouched.
/// </summary>
public sealed class TagDecimalsTests
{
    private static BridgeValue Value(object? v) =>
        new("default", "Tag1", v, DateTime.UtcNow, 192, true);

    private static TagMapping Mapping(int? decimals) => new() { Decimals = decimals };

    [Theory]
    [InlineData(3.14159265, 2, 3.14)]
    [InlineData(3.14159265, 0, 3.0)]
    [InlineData(2.5, 0, 3.0)]        // AwayFromZero, not banker's rounding
    [InlineData(3.5, 0, 4.0)]
    [InlineData(3.14159265, 15, 3.14159265)]
    public void Apply_RoundsDoubleToDigits(double raw, int digits, double expected)
    {
        BridgeValue rounded = TagDecimals.Apply(Value(raw), Mapping(digits));

        double result = Assert.IsType<double>(rounded.Value);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Apply_RoundsFloatToDigits()
    {
        BridgeValue rounded = TagDecimals.Apply(Value(3.14159f), Mapping(2));

        float result = Assert.IsType<float>(rounded.Value);
        Assert.Equal(3.14f, result);
    }

    [Fact]
    public void Apply_NullDecimals_PassesThrough()
    {
        double raw = 3.14159265;

        BridgeValue result = TagDecimals.Apply(Value(raw), Mapping(null));

        Assert.Equal(raw, result.Value);
    }

    [Theory]
    [InlineData(-5)]   // legacy sentinel from before the null-based design
    [InlineData(-1)]
    public void Apply_NegativeDecimals_TreatedAsNoRounding(int digits)
    {
        double raw = 3.14159265;

        BridgeValue result = TagDecimals.Apply(Value(raw), Mapping(digits));

        Assert.Equal(raw, result.Value);
    }

    [Fact]
    public void Apply_KeepsValueTypeFloat()
    {
        BridgeValue rounded = TagDecimals.Apply(Value(1234.5678f), Mapping(2));

        Assert.IsType<float>(rounded.Value);
    }

    [Theory]
    [InlineData(42)]
    [InlineData("text")]
    [InlineData(null)]
    public void Apply_NonFloatingPoint_PassesThrough(object? raw)
    {
        BridgeValue result = TagDecimals.Apply(Value(raw), Mapping(2));

        Assert.Equal(raw, result.Value);
    }

    [Fact]
    public void Apply_NullMapping_PassesThrough()
    {
        BridgeValue result = TagDecimals.Apply(Value(3.14159), null);

        Assert.Equal(3.14159, result.Value);
    }
}
