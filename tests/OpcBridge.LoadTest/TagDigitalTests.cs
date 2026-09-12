using System.Text.Json;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Per-tag digital (two-state) semantics: Boolean tags resolve digital automatically,
/// byte/numeric tags only when explicitly marked, and any non-zero value coerces to on.
/// </summary>
public sealed class TagDigitalTests
{
    private static TagMapping Mapping(string dataType, bool? digital = null) =>
        new() { DataType = dataType, Digital = digital };

    [Theory]
    [InlineData("Boolean")]
    [InlineData("boolean")]
    [InlineData("BOOL")]
    [InlineData("Bool")]
    public void Resolve_BooleanType_IsDigitalAutomatically(string dataType)
    {
        Assert.True(TagDigital.Resolve(Mapping(dataType)));
    }

    [Theory]
    [InlineData("Byte")]
    [InlineData("Int32")]
    [InlineData("Double")]
    [InlineData("String")]
    [InlineData("Auto")]
    public void Resolve_NonBooleanType_IsAnalogByDefault(string dataType)
    {
        Assert.False(TagDigital.Resolve(Mapping(dataType)));
    }

    [Fact]
    public void Resolve_ByteTagExplicitlyMarked_IsDigital()
    {
        // The real-world case: a Byte tag used only as a 0/1 flag.
        Assert.True(TagDigital.Resolve(Mapping("Byte", digital: true)));
    }

    [Fact]
    public void Resolve_BooleanTagExplicitlyAnalog_StaysAnalog()
    {
        Assert.False(TagDigital.Resolve(Mapping("Boolean", digital: false)));
    }

    [Fact]
    public void Resolve_NullMapping_IsAnalog()
    {
        Assert.False(TagDigital.Resolve(null));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData((byte)1, true)]
    [InlineData((byte)0, false)]
    [InlineData((sbyte)1, true)]
    [InlineData((short)5, true)]
    [InlineData((ushort)0, false)]
    [InlineData(255, true)]
    [InlineData(0, false)]
    [InlineData((uint)1, true)]
    [InlineData((long)1, true)]
    [InlineData((ulong)0, false)]
    [InlineData(1.0f, true)]
    [InlineData(0.0f, false)]
    [InlineData(0.5, true)]
    [InlineData(0.0, false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void CoerceBool_Primitives_MapNonZeroToOn(object? value, bool expected)
    {
        Assert.Equal(expected, TagDigital.CoerceBool(value));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("nonsense", false)]
    public void CoerceBool_Unparseable_IsOff(object? value, bool expected)
    {
        Assert.Equal(expected, TagDigital.CoerceBool(value));
    }

    [Fact]
    public void CoerceBool_JsonBoolean_IsRecognized()
    {
        // HMI live values arrive as JsonElement after wire deserialization.
        Assert.True(TagDigital.CoerceBool(JsonDocument.Parse("true").RootElement));
        Assert.False(TagDigital.CoerceBool(JsonDocument.Parse("false").RootElement));
    }

    [Fact]
    public void CoerceBool_JsonNumber_IsRecognized()
    {
        Assert.True(TagDigital.CoerceBool(JsonDocument.Parse("1").RootElement));
        Assert.True(TagDigital.CoerceBool(JsonDocument.Parse("7").RootElement));
        Assert.False(TagDigital.CoerceBool(JsonDocument.Parse("0").RootElement));
    }

    [Fact]
    public void CoerceBool_JsonString_IsRecognized()
    {
        Assert.True(TagDigital.CoerceBool(JsonDocument.Parse("\"1\"").RootElement));
        Assert.False(TagDigital.CoerceBool(JsonDocument.Parse("\"0\"").RootElement));
    }
}
