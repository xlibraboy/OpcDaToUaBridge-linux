using OpcBridge.Hmi.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class TrendScaleTests
{
    [Fact]
    public void Auto_EnclosesDataWithNiceStep()
    {
        TrendAxis axis = TrendScale.Resolve(
            autoRange: true,
            typeRange: null,
            dataMin: 3,
            dataMax: 17);

        Assert.True(axis.IsValid);
        Assert.True(axis.Min <= 3);
        Assert.True(axis.Max >= 17);
        Assert.Equal(0, axis.Min);
        Assert.Equal(20, axis.Max);
        Assert.Equal(5, axis.Step);
    }

    [Fact]
    public void Auto_EnclosesNegativeData()
    {
        TrendAxis axis = TrendScale.Resolve(true, null, -100, -98);

        Assert.True(axis.IsValid);
        Assert.True(axis.Min <= -100);
        Assert.True(axis.Max >= -98);
        Assert.InRange((axis.Max - axis.Min) / axis.Step, 1, 12);
    }

    [Fact]
    public void Fixed_PinsToTypeRange()
    {
        TrendAxis axis = TrendScale.Resolve(
            autoRange: false,
            typeRange: (0, 255),
            dataMin: 100,
            dataMax: 110);

        Assert.True(axis.IsValid);
        Assert.Equal(0, axis.Min);
        Assert.Equal(255, axis.Max);
    }

    [Fact]
    public void Fixed_WithoutTypeRange_AutoFits()
    {
        TrendAxis axis = TrendScale.Resolve(
            autoRange: false,
            typeRange: null,
            dataMin: 3,
            dataMax: 17);

        Assert.True(axis.IsValid);
        Assert.Equal(0, axis.Min);
        Assert.Equal(20, axis.Max);
        Assert.Equal(5, axis.Step);
    }

    [Fact]
    public void Degenerate_FlatSeries_FallsBackToTypeRange()
    {
        TrendAxis axis = TrendScale.Resolve(
            autoRange: true,
            typeRange: (0, 255),
            dataMin: 42,
            dataMax: 42);

        Assert.True(axis.IsValid);
        Assert.Equal(0, axis.Min);
        Assert.Equal(255, axis.Max);
    }

    [Fact]
    public void Degenerate_FlatDouble_UsesBandAroundValue()
    {
        TrendAxis axis = TrendScale.Resolve(
            autoRange: true,
            typeRange: null,
            dataMin: 55.5,
            dataMax: 55.5);

        Assert.True(axis.IsValid);
        Assert.True(axis.Min < 55.5);
        Assert.True(axis.Max > 55.5);
        Assert.True(axis.Max - axis.Min < 50);
    }

    [Fact]
    public void FromTypeRange_Boolean_GivesZeroToOneAxis()
    {
        TrendAxis axis = TrendScale.FromTypeRange((0, 1));

        Assert.True(axis.IsValid);
        Assert.Equal(0, axis.Min);
        Assert.Equal(1, axis.Max);
        Assert.True(axis.Step > 0);
    }

    [Fact]
    public void Resolve_NoSamples_IsInvalid()
    {
        TrendAxis axis = TrendScale.Resolve(true, (0, 255), null, null);
        Assert.False(axis.IsValid);
    }
}

public sealed class TrendTimeAxisTests
{
    [Fact]
    public void OneHour_UsesTenMinuteTicks()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), TrendTimeAxis.StepFor(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void EightHours_UsesTwoHourTicks()
    {
        Assert.Equal(TimeSpan.FromHours(2), TrendTimeAxis.StepFor(TimeSpan.FromHours(8)));
    }

    [Fact]
    public void TwentyFourHours_UsesFourHourTicks()
    {
        Assert.Equal(TimeSpan.FromHours(4), TrendTimeAxis.StepFor(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void Floor_AlignsToStepBoundary()
    {
        DateTime time = new(2026, 8, 5, 13, 47, 22, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 8, 5, 13, 40, 0, DateTimeKind.Utc),
            TrendTimeAxis.Floor(time, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void StepFor_NonPositiveSpan_ReturnsZero()
    {
        Assert.Equal(TimeSpan.Zero, TrendTimeAxis.StepFor(TimeSpan.Zero));
    }
}
