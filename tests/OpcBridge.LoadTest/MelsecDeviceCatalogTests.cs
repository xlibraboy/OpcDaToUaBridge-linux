using System.Linq;
using OpcBridge.Drivers.Melsec.Addressing;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The device catalog is what the dashboard shows as "accepted PLC addresses" for
/// MELSEC sources (serial + MX Component). These tests pin two contracts:
/// 1. the catalog rows themselves (what the UI will render), and
/// 2. parser ↔ catalog consistency — the ranges shown must be exactly the ranges
///    MelsecAddressParser enforces, at both boundaries.
/// </summary>
public sealed class MelsecDeviceCatalogTests
{
    [Fact]
    public void Catalog_ListsAllSupportedDevicesInOrder()
    {
        Assert.Equal(
            new[] { "D", "M", "X", "Y", "TS", "TC", "TN", "CS", "CC", "CN" },
            MelsecDeviceCatalog.Devices.Select(d => d.Device).ToArray());
    }

    [Theory]
    [InlineData("D", 1023, true)]
    [InlineData("M", 2047, false)]
    [InlineData("X", 2047, false)] // decimal cap; octal form is 3777
    [InlineData("Y", 2047, false)]
    [InlineData("TS", 2047, false)]
    [InlineData("TC", 2047, false)]
    [InlineData("TN", 2047, false)]
    [InlineData("CS", 2047, false)]
    [InlineData("CC", 2047, false)]
    [InlineData("CN", 2047, false)]
    public void Catalog_MaxNumbersMatchAppLimits(string device, int max, bool bitSuffixAllowed)
    {
        MelsecDeviceRange range = MelsecDeviceCatalog.Find(device);

        Assert.Equal(max, range.MaxNumber);
        Assert.Equal(bitSuffixAllowed, range.BitSuffixAllowed);
    }

    [Fact]
    public void Catalog_NumberBases_DecimalExceptXY()
    {
        Assert.Equal(MelsecNumberBase.Decimal, MelsecDeviceCatalog.Find("D").NumberBase);
        Assert.Equal(MelsecNumberBase.Decimal, MelsecDeviceCatalog.Find("M").NumberBase);
        Assert.Equal(MelsecNumberBase.OctalOrHex, MelsecDeviceCatalog.Find("X").NumberBase);
        Assert.Equal(MelsecNumberBase.OctalOrHex, MelsecDeviceCatalog.Find("Y").NumberBase);
        Assert.Equal(MelsecNumberBase.Decimal, MelsecDeviceCatalog.Find("TN").NumberBase);
        Assert.Equal(MelsecNumberBase.Decimal, MelsecDeviceCatalog.Find("CN").NumberBase);
    }

    [Fact]
    public void Catalog_SignalTypes_MatchDeviceFamilies()
    {
        Assert.Equal("Word", MelsecDeviceCatalog.Find("D").SignalType);
        Assert.Equal("Bit", MelsecDeviceCatalog.Find("M").SignalType);
        Assert.Equal("Bit", MelsecDeviceCatalog.Find("X").SignalType);
        Assert.Equal("Bit", MelsecDeviceCatalog.Find("Y").SignalType);
        Assert.Equal("Bit", MelsecDeviceCatalog.Find("TS").SignalType);
        Assert.Equal("Bit", MelsecDeviceCatalog.Find("TC").SignalType);
        Assert.Equal("Word", MelsecDeviceCatalog.Find("TN").SignalType);
        Assert.Equal("Bit", MelsecDeviceCatalog.Find("CS").SignalType);
        Assert.Equal("Bit", MelsecDeviceCatalog.Find("CC").SignalType);
        Assert.Equal("Word", MelsecDeviceCatalog.Find("CN").SignalType);
    }

    [Fact]
    public void Catalog_TnAndCnExposeSingleLetterAliases()
    {
        Assert.Equal(new[] { "T" }, MelsecDeviceCatalog.Find("TN").Aliases);
        Assert.Equal(new[] { "C" }, MelsecDeviceCatalog.Find("CN").Aliases);
        Assert.Empty(MelsecDeviceCatalog.Find("D").Aliases);
        Assert.Empty(MelsecDeviceCatalog.Find("M").Aliases);
    }

    [Fact]
    public void Catalog_EveryRowHasExampleAndDisplayName()
    {
        foreach (MelsecDeviceRange row in MelsecDeviceCatalog.Devices)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.DisplayName), row.Device + " needs DisplayName");
            Assert.False(string.IsNullOrWhiteSpace(row.Example), row.Device + " needs Example");
        }
    }

    [Fact]
    public void Catalog_DRow_CarriesMaxBitIndex15()
    {
        Assert.Equal(15, MelsecDeviceCatalog.Find("D").MaxBitIndex);
    }

    [Theory]
    [InlineData("D")]
    [InlineData("M")]
    [InlineData("TS")]
    [InlineData("TC")]
    [InlineData("TN")]
    [InlineData("CS")]
    [InlineData("CC")]
    [InlineData("CN")]
    public void Parser_AcceptsDecimalCatalogMax_AndRejectsBeyond(string device)
    {
        MelsecDeviceRange range = MelsecDeviceCatalog.Find(device);

        Assert.True(
            MelsecAddressParser.TryParse($"{device}{range.MaxNumber}", out _, out string acceptError),
            $"{device}{range.MaxNumber} should parse but got: {acceptError}");
        Assert.False(
            MelsecAddressParser.TryParse($"{device}{range.MaxNumber + 1}", out _, out _),
            $"{device}{range.MaxNumber + 1} should be rejected");
    }

    [Theory]
    [InlineData("X")]
    [InlineData("Y")]
    public void Parser_AcceptsOctalCatalogMax_AndRejectsBeyond(string device)
    {
        // Catalog stores the numeric cap in decimal (2047 = 3777 octal); X/Y are entered in octal.
        MelsecDeviceRange range = MelsecDeviceCatalog.Find(device);
        string maxOctal = Convert.ToString(range.MaxNumber, 8);

        Assert.True(
            MelsecAddressParser.TryParse($"{device}{maxOctal}", out _, out string acceptError),
            $"{device}{maxOctal} should parse but got: {acceptError}");
        Assert.False(
            MelsecAddressParser.TryParse($"{device}4000", out _, out _),
            $"{device}4000 (octal) exceeds the cap and should be rejected");
    }

    [Fact]
    public void Parser_BitSuffixOnD_RespectsCatalogMaxBitIndex()
    {
        int maxBit = MelsecDeviceCatalog.Find("D").MaxBitIndex!.Value;

        Assert.True(MelsecAddressParser.TryParse($"D0:{maxBit}", out _, out _));
        Assert.False(MelsecAddressParser.TryParse($"D0:{maxBit + 1}", out _, out _));
    }

    [Fact]
    public void Parser_RejectsUnknownDevice_NotPresentInCatalog()
    {
        Assert.False(MelsecAddressParser.TryParse("W100", out _, out _));
    }
}
