using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// <see cref="PortRange"/> is the per-install port block: the config form is "start-end", the
/// API form is the record's own start/end JSON. These pin the parsing and overlap rules that
/// startup and POST /api/ports/config rely on, so a typo in a plant config falls back with a
/// reported problem instead of moving the bridge somewhere unexpected.
/// </summary>
public sealed class PortRangeTests
{
    [Theory]
    [InlineData("8080-8180", 8080, 8180)]
    [InlineData("9000-9010", 9000, 9010)]
    [InlineData(" 9000 - 9010 ", 9000, 9010)]
    [InlineData("1-65535", 1, 65535)]
    [InlineData("4840-4840", 4840, 4840)]
    public void TryParse_AcceptsTheStartEndForm(string text, int start, int end)
    {
        Assert.True(PortRange.TryParse(text, out PortRange range));

        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
        Assert.True(range.IsValid);
        Assert.Equal($"{start}-{end}", range.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("8080")]
    [InlineData("8080-")]
    [InlineData("-8180")]
    [InlineData("8080-8180-9000")]
    [InlineData("http-8180")]
    [InlineData("8180-8080")]
    [InlineData("0-8180")]
    [InlineData("8080-65536")]
    public void TryParse_RejectsMalformedText(string? text)
    {
        Assert.False(PortRange.TryParse(text, out PortRange range));
        Assert.False(range.IsValid);
    }

    [Fact]
    public void Defaults_AreTheStandardBlocks()
    {
        Assert.Equal(new PortRange(8080, 8180), PortRange.DefaultHttp);
        Assert.Equal(new PortRange(4840, 4940), PortRange.DefaultUa);
    }

    [Fact]
    public void Contains_IncludesBothBounds()
    {
        PortRange range = new(9000, 9010);

        Assert.True(range.Contains(9000));
        Assert.True(range.Contains(9005));
        Assert.True(range.Contains(9010));
        Assert.False(range.Contains(8999));
        Assert.False(range.Contains(9011));
        Assert.False(new PortRange(9180, 9000).Contains(9005));
    }

    [Fact]
    public void Length_CountsTheWholeBlock()
    {
        Assert.Equal(11, new PortRange(9000, 9010).Length);
        Assert.Equal(0, new PortRange(9010, 9000).Length);
    }

    [Fact]
    public void Overlaps_DetectsSharedAndTouchingBlocks()
    {
        PortRange http = new(8080, 8180);

        Assert.True(http.Overlaps(new PortRange(8180, 8280)));
        Assert.True(http.Overlaps(new PortRange(8080, 8080)));
        Assert.True(http.Overlaps(new PortRange(8000, 9000)));
        Assert.False(http.Overlaps(new PortRange(8181, 8280)));
        Assert.False(http.Overlaps(new PortRange(4840, 4940)));
        Assert.False(http.Overlaps(new PortRange(9000, 8080)));
    }
}
