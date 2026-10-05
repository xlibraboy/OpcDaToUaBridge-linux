using OpcBridge.Hmi.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class TrendNumberFormatTests
{
    [Theory]
    [InlineData(12.34, "12.34")]
    [InlineData(12.3, "12.3")]
    [InlineData(12.0, "12")]
    [InlineData(0.0, "0")]
    [InlineData(-2.5, "-2.5")]
    public void Format_TrimsTrailingZeros(double value, string expected)
    {
        Assert.Equal(expected, TrendNumberFormat.Format(value));
    }

    [Theory]
    [InlineData(3.14159, "3.142")]
    [InlineData(1.23456, "1.235")]
    public void Format_RoundsToThreeDecimals(double value, string expected)
    {
        Assert.Equal(expected, TrendNumberFormat.Format(value));
    }

    [Fact]
    public void Format_HidesFloatWideningNoise()
    {
        // The bridge rounds a tag to 2 decimals as a float and InfluxDB stores it widened to
        // float64. The trend must read 12.34, not 12.340000152587891.
        Assert.Equal("12.34", TrendNumberFormat.Format((double)12.34f));
        Assert.Equal("0.1", TrendNumberFormat.Format((double)0.1f));
    }

    [Fact]
    public void Format_SmallNegative_IsPlainZero()
    {
        Assert.Equal("0", TrendNumberFormat.Format(-0.0001));
        Assert.Equal("0", TrendNumberFormat.Format(-0.0));
    }
}
