using OpcBridge.Drivers.Melsec.Addressing;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class MelsecAddressTests
{
    [Theory]
    [InlineData("D100", "D100", MelsecDeviceKind.D, 100, null)]
    [InlineData("d100:8", "D100:8", MelsecDeviceKind.D, 100, 8)]
    [InlineData("D100.8", "D100:8", MelsecDeviceKind.D, 100, 8)]
    [InlineData("M10", "M10", MelsecDeviceKind.M, 10, null)]
    [InlineData("X20", "X020", MelsecDeviceKind.X, 16, null)] // 20 octal = 16
    [InlineData("Y0F", "Y00F", MelsecDeviceKind.Y, 15, null)]
    public void TryParse_Valid(string input, string canonical, MelsecDeviceKind kind, int number, int? bit)
    {
        Assert.True(MelsecAddressParser.TryParse(input, out var addr, out _));
        Assert.Equal(canonical, addr.Canonical);
        Assert.Equal(kind, addr.Device);
        Assert.Equal(number, addr.Number);
        Assert.Equal(bit, addr.BitIndex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("W100")]
    [InlineData("D1024")]
    [InlineData("M2048")]
    [InlineData("X8")] // invalid octal digit
    [InlineData("D100:16")]
    [InlineData("M10:1")]
    public void TryParse_Invalid(string input)
    {
        Assert.False(MelsecAddressParser.TryParse(input, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
