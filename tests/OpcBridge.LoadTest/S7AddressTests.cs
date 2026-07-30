using OpcBridge.Drivers.S7.Addressing;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class S7AddressTests
{
    [Theory]
    [InlineData("I0.0", "I0.0", S7Area.Inputs, 0, 1, 0)]
    [InlineData("q0.1", "Q0.1", S7Area.Outputs, 0, 1, 1)]
    [InlineData("M10.2", "M10.2", S7Area.Flags, 10, 1, 2)]
    [InlineData("IB0", "IB0", S7Area.Inputs, 0, 1, null)]
    [InlineData("QB0", "QB0", S7Area.Outputs, 0, 1, null)]
    [InlineData("MB0", "MB0", S7Area.Flags, 0, 1, null)]
    [InlineData("VB10", "VB10", S7Area.V, 10, 1, null)]
    [InlineData("IW2", "IW2", S7Area.Inputs, 2, 2, null)]
    [InlineData("QW4", "QW4", S7Area.Outputs, 4, 2, null)]
    [InlineData("MW8", "MW8", S7Area.Flags, 8, 2, null)]
    [InlineData("vw100", "VW100", S7Area.V, 100, 2, null)]
    [InlineData("ID0", "ID0", S7Area.Inputs, 0, 4, null)]
    [InlineData("QD0", "QD0", S7Area.Outputs, 0, 4, null)]
    [InlineData("MD12", "MD12", S7Area.Flags, 12, 4, null)]
    [InlineData("VD200", "VD200", S7Area.V, 200, 4, null)]
    public void TryParse_Valid(
        string input,
        string canonical,
        S7Area area,
        int byteOffset,
        int sizeBytes,
        int? bit)
    {
        Assert.True(S7AddressParser.TryParse(input, out var addr, out _));
        Assert.Equal(canonical, addr.Canonical);
        Assert.Equal(area, addr.Area);
        Assert.Equal(byteOffset, addr.ByteOffset);
        Assert.Equal(sizeBytes, addr.SizeBytes);
        Assert.Equal(bit, addr.BitIndex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("T0")]
    [InlineData("I0.8")]
    [InlineData("I0.-1")]
    [InlineData("VX0")]
    [InlineData("I")]
    [InlineData("IB")]
    [InlineData("I0")]
    [InlineData("VB1A")]
    [InlineData("I0.0.1")]
    public void TryParse_Invalid(string input)
    {
        Assert.False(S7AddressParser.TryParse(input, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Canonicalize_Valid_ReturnsCanonical()
    {
        Assert.Equal("VW100", S7AddressParser.Canonicalize("vw100"));
    }

    [Fact]
    public void Canonicalize_Invalid_Throws()
    {
        Assert.Throws<FormatException>(() => S7AddressParser.Canonicalize("T0"));
    }
}
