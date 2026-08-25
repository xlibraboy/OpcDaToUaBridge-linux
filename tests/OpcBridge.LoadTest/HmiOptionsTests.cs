using OpcBridge.App.Hmi;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class HmiOptionsTests
{
    [Theory]
    [InlineData(0, 50)]
    [InlineData(49, 50)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(1000, 1000)]
    [InlineData(1001, 1000)]
    [InlineData(5000, 1000)]
    public void ClampBroadcastFlushMs_ClampsToRange(int input, int expected)
    {
        Assert.Equal(expected, HmiOptions.ClampBroadcastFlushMs(input));
    }

    [Fact]
    public void GetClampedBroadcastFlushMs_UsesProperty()
    {
        var options = new HmiOptions { BroadcastFlushMs = 25 };
        Assert.Equal(50, options.GetClampedBroadcastFlushMs());
    }
}
