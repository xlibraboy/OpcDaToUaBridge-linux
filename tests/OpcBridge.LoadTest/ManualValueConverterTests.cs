using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Manual (simulation) value parsing must keep following the tag's actual type. When a
/// mapping's DataType is "Auto" and the tag already has a real value, the typed text is
/// parsed into that value's CLR type — never re-inferred from the text (which changed
/// e.g. a Double/Int32 tag to Int64 as soon as the operator typed a whole number).
/// Generic inference is only the fallback for tags with no prior value at all.
/// </summary>
public sealed class ManualValueConverterTests
{
    [Fact]
    public void Convert_ExplicitBoolean_AcceptsTrueFalseOneZero()
    {
        Assert.True(ManualValueConverter.TryConvert("Boolean", "true", null, out object? yes));
        Assert.Equal(true, Assert.IsType<bool>(yes));

        Assert.True(ManualValueConverter.TryConvert("Bool", "1", null, out object? one));
        Assert.Equal(true, Assert.IsType<bool>(one));

        Assert.True(ManualValueConverter.TryConvert("Boolean", "0", null, out object? zero));
        Assert.Equal(false, Assert.IsType<bool>(zero));
    }

    [Fact]
    public void Convert_ExplicitInt32_ParsesIntegerText()
    {
        Assert.True(ManualValueConverter.TryConvert("Int32", "42", null, out object? value));
        Assert.Equal(42, Assert.IsType<int>(value));
    }

    [Fact]
    public void Convert_ExplicitTypeFailure_RejectsInsteadOfReTyping()
    {
        // A declared numeric type that cannot parse the text must not silently become String.
        Assert.False(ManualValueConverter.TryConvert("Int32", "abc", null, out _));
        Assert.False(ManualValueConverter.TryConvert("Boolean", "5", null, out _));
    }

    [Fact]
    public void Convert_AutoWithRealDoubleValue_KeepsDoubleType()
    {
        double referenceValue = 108.51;

        Assert.True(ManualValueConverter.TryConvert("Auto", "5", referenceValue, out object? value));
        Assert.Equal(5.0, Assert.IsType<double>(value));

        Assert.True(ManualValueConverter.TryConvert("Auto", "5.75", referenceValue, out object? fraction));
        Assert.Equal(5.75, Assert.IsType<double>(fraction));
    }

    [Fact]
    public void Convert_AutoWithRealInt32Value_KeepsInt32Type()
    {
        int referenceValue = 42;

        Assert.True(ManualValueConverter.TryConvert("Auto", "5", referenceValue, out object? value));
        Assert.Equal(5, Assert.IsType<int>(value));
    }

    [Fact]
    public void Convert_AutoWithRealByteValue_RespectsRange()
    {
        byte referenceValue = 1;

        Assert.True(ManualValueConverter.TryConvert("Auto", "200", referenceValue, out object? value));
        Assert.Equal((byte)200, Assert.IsType<byte>(value));

        // Out of the actual type's range: reject rather than widening to Int64.
        Assert.False(ManualValueConverter.TryConvert("Auto", "300", referenceValue, out _));
    }

    [Fact]
    public void Convert_AutoWithRealBooleanValue_KeepsBooleanType()
    {
        bool referenceValue = true;

        Assert.True(ManualValueConverter.TryConvert("Auto", "1", referenceValue, out object? value));
        Assert.Equal(true, Assert.IsType<bool>(value));

        // Not a bool: reject instead of publishing a differently-typed value.
        Assert.False(ManualValueConverter.TryConvert("Auto", "5", referenceValue, out _));
    }

    [Fact]
    public void Convert_AutoWithRealStringValue_StaysString()
    {
        string referenceValue = "off";

        Assert.True(ManualValueConverter.TryConvert("Auto", "42", referenceValue, out object? value));
        Assert.Equal("42", Assert.IsType<string>(value));
    }

    [Fact]
    public void Convert_AutoWithRealFloatValue_KeepsFloatType()
    {
        float referenceValue = 1.5f;

        Assert.True(ManualValueConverter.TryConvert("Auto", "5", referenceValue, out object? value));
        Assert.Equal(5.0f, Assert.IsType<float>(value));
    }

    [Fact]
    public void Convert_AutoWithRealDateTimeValue_ParsesTimestamp()
    {
        DateTime referenceValue = new(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(ManualValueConverter.TryConvert("Auto", "2026-09-05T12:30:00Z", referenceValue, out object? value));
        DateTime parsed = Assert.IsType<DateTime>(value);
        Assert.Equal(new DateTime(2026, 9, 5, 12, 30, 0, DateTimeKind.Utc), parsed);
    }

    [Fact]
    public void Convert_AutoWithoutReference_FallsBackToLegacyInference()
    {
        // Fresh Manual tag (never read): no actual type to follow — infer from the text.
        Assert.True(ManualValueConverter.TryConvert("Auto", "42", null, out object? whole));
        Assert.Equal(42L, Assert.IsType<long>(whole));

        Assert.True(ManualValueConverter.TryConvert("Auto", "4.5", null, out object? fraction));
        Assert.Equal(4.5, Assert.IsType<double>(fraction));

        Assert.True(ManualValueConverter.TryConvert("Auto", "true", null, out object? flag));
        Assert.Equal(true, Assert.IsType<bool>(flag));

        Assert.True(ManualValueConverter.TryConvert("Auto", "hello", null, out object? text));
        Assert.Equal("hello", Assert.IsType<string>(text));
    }
}
