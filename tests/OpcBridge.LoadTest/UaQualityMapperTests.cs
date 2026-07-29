using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class UaQualityMapperTests
{
    [Theory]
    [InlineData(0u, 0xC0, true)]          // Good
    [InlineData(0x40000000u, 0x40, false)] // Uncertain (StatusCode.Uncertain)
    [InlineData(0x80000000u, 0x00, false)] // Bad
    public void FromStatusCode_MapsClasses(uint code, int expectedQuality, bool expectedGood)
    {
        var (q, good) = UaQualityMapper.FromStatusCode(code);
        Assert.Equal(expectedQuality, q);
        Assert.Equal(expectedGood, good);
    }
}
