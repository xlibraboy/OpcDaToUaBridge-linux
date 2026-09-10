using System.Globalization;
using OpcBridge.Hmi.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class TrendSeriesTests
{
    [Fact]
    public void Palette_FirstColor_IsSingleTagDefault()
    {
        Assert.Equal(TrendSeriesPalette.Colors[0], TrendSeriesPalette.ColorFor(0));
    }

    [Fact]
    public void Palette_CyclesAfterEnd()
    {
        Assert.Equal(TrendSeriesPalette.Colors[0], TrendSeriesPalette.ColorFor(TrendSeriesPalette.Colors.Length));
        Assert.Equal(TrendSeriesPalette.Colors[1], TrendSeriesPalette.ColorFor(TrendSeriesPalette.Colors.Length + 1));
    }

    [Fact]
    public void Palette_NegativeIndex_StaysInRange()
    {
        Assert.Equal(TrendSeriesPalette.Colors[^1], TrendSeriesPalette.ColorFor(-1));
    }

    [Fact]
    public void Palette_ColorsAreDistinctHexStrings()
    {
        Assert.Equal(TrendSeriesPalette.Colors.Length, TrendSeriesPalette.Colors.Distinct().Count());
        foreach (string color in TrendSeriesPalette.Colors)
        {
            Assert.Matches("^#[0-9A-Fa-f]{6}$", color);
        }
    }

    [Fact]
    public void TrendSeries_KeepsRenderingSettings()
    {
        TrendSample[] samples = { new(new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc), 1), new(new DateTime(2026, 8, 5, 12, 1, 0, DateTimeKind.Utc), 2) };
        var series = new TrendSeries("Tank.Level", "m", "Step", "#F48FB1", samples, Description: "Level of Tank 1");

        Assert.Equal("Tank.Level", series.Name);
        Assert.Equal("m", series.Unit);
        Assert.Equal("Step", series.TrendStyle);
        Assert.Equal("#F48FB1", series.Color);
        Assert.Equal("Level of Tank 1", series.Description);
        Assert.Equal(2, series.Samples.Count);
        Assert.True(series.Visible);
        Assert.False(series.IsBoolean);
    }

    [Fact]
    public void TrendSeries_HiddenBoolean_FlagsCarry()
    {
        var series = new TrendSeries("Pump.Run", "", "Continuous", "#A5D6A7", Array.Empty<TrendSample>(), Visible: false, IsBoolean: true);
        Assert.False(series.Visible);
        Assert.True(series.IsBoolean);
    }
}

public sealed class TrendRangeTests
{
    [Theory]
    [InlineData("15m", 0.25)]
    [InlineData("1h", 1.0)]
    [InlineData("8h", 8.0)]
    [InlineData("24h", 24.0)]
    [InlineData("2", 2.0)]
    [InlineData("", 1.0)]
    [InlineData(null, 1.0)]
    [InlineData("bogus", 1.0)]
    public void ParseHours_HandlesLabels(string? label, double expectedHours)
    {
        Assert.Equal(expectedHours, TrendRange.ParseHours(label), 6);
    }

    [Theory]
    [InlineData(0.25, "15m")]
    [InlineData(1.0, "1h")]
    [InlineData(8.0, "8h")]
    [InlineData(24.0, "24h")]
    public void Normalize_RoundTripsLabels(double hours, string expected)
    {
        Assert.Equal(expected, TrendRange.Normalize(hours));
    }
}

public sealed class TrendPenStatsTests
{
    private static readonly DateTime Base = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Compute_MinsMaxAvgDeltaOverWindow()
    {
        TrendSample[] samples =
        {
            new(Base, 10),
            new(Base.AddMinutes(1), 20),
            new(Base.AddMinutes(2), 30)
        };

        TrendPenStats.Result? stats = TrendPenStats.Compute(samples, Base, Base.AddMinutes(5));

        Assert.NotNull(stats);
        Assert.Equal(30, stats!.Value.Value); // last sample
        Assert.Equal(10, stats.Value.Minimum);
        Assert.Equal(30, stats.Value.Maximum);
        Assert.Equal(20, stats.Value.Average, 6);
        Assert.Equal(20, stats.Value.Delta, 6);
        Assert.Equal(3, stats.Value.Count);
    }

    [Fact]
    public void Compute_ExcludesSamplesOutsideWindow()
    {
        TrendSample[] samples =
        {
            new(Base.AddMinutes(-10), 100), // before the window
            new(Base, 10),
            new(Base.AddMinutes(1), 20)
        };

        TrendPenStats.Result? stats = TrendPenStats.Compute(samples, Base, Base.AddMinutes(5));

        Assert.NotNull(stats);
        Assert.Equal(10, stats!.Value.Minimum);
        Assert.Equal(20, stats.Value.Maximum);
        Assert.Equal(2, stats.Value.Count);
    }

    [Fact]
    public void Compute_NoWindowSamples_FallsBackToLastOverall()
    {
        TrendSample[] samples = { new(Base.AddMinutes(-10), 42) };

        TrendPenStats.Result? stats = TrendPenStats.Compute(samples, Base, Base.AddMinutes(5));

        Assert.NotNull(stats);
        Assert.Equal(42, stats!.Value.Value);
        Assert.Equal(42, stats.Value.Minimum);
        Assert.Equal(0, stats.Value.Count);
    }

    [Fact]
    public void Compute_NoSamplesAtAll_ReturnsNull()
    {
        Assert.Null(TrendPenStats.Compute(Array.Empty<TrendSample>(), Base, Base.AddMinutes(5)));
    }

    [Fact]
    public void Compute_IgnoresNonFiniteSamples()
    {
        TrendSample[] samples =
        {
            new(Base, double.NaN),
            new(Base.AddMinutes(1), 15)
        };

        TrendPenStats.Result? stats = TrendPenStats.Compute(samples, Base, Base.AddMinutes(5));

        Assert.NotNull(stats);
        Assert.Equal(15, stats!.Value.Minimum);
        Assert.Equal(15, stats.Value.Maximum);
        Assert.Equal(1, stats.Value.Count);
    }

    [Fact]
    public void Compute_StdDevOfConstantSeries_IsZero()
    {
        TrendSample[] samples = { new(Base, 5), new(Base.AddMinutes(1), 5), new(Base.AddMinutes(2), 5) };

        TrendPenStats.Result? stats = TrendPenStats.Compute(samples, Base, Base.AddMinutes(5));

        Assert.NotNull(stats);
        Assert.Equal(0, stats!.Value.StdDev, 6);
    }

    [Fact]
    public void Compute_StdDevOfKnownSpread()
    {
        // Values 2 and 4: mean 3, population variance 1, stddev 1.
        TrendSample[] samples = { new(Base, 2), new(Base.AddMinutes(1), 4) };

        TrendPenStats.Result? stats = TrendPenStats.Compute(samples, Base, Base.AddMinutes(5));

        Assert.NotNull(stats);
        Assert.Equal(1, stats!.Value.StdDev, 6);
    }
}

public sealed class TrendPercentAxisTests
{
    [Fact]
    public void PercentFor_MapsMinToZeroMaxToHundred()
    {
        Assert.Equal(0, TrendPercentAxis.PercentFor(10, 10, 20), 6);
        Assert.Equal(100, TrendPercentAxis.PercentFor(20, 10, 20), 6);
        Assert.Equal(50, TrendPercentAxis.PercentFor(15, 10, 20), 6);
    }

    [Fact]
    public void PercentFor_ClampsOutsideRange()
    {
        Assert.Equal(0, TrendPercentAxis.PercentFor(5, 10, 20), 6);
        Assert.Equal(100, TrendPercentAxis.PercentFor(25, 10, 20), 6);
    }

    [Fact]
    public void PercentFor_FlatSeries_MapsToMidpoint()
    {
        Assert.Equal(50, TrendPercentAxis.PercentFor(7, 7, 7), 6);
    }

    [Fact]
    public void PercentFor_NoRange_MapsToMidpoint()
    {
        Assert.Equal(50, TrendPercentAxis.PercentFor(3, double.NaN, double.NaN), 6);
    }
}

public sealed class TrendCsvTests
{
    [Fact]
    public void Build_EmitsLongFormatRows()
    {
        TrendSample[] samples =
        {
            new(new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc), 12.5),
            new(new DateTime(2026, 8, 5, 12, 1, 0, DateTimeKind.Utc), 13.25)
        };
        var series = new[] { new TrendSeries("Tank.Level", "m", "Continuous", "#4FC3F7", samples) };

        string csv = TrendCsv.Build(series);

        string[] lines = csv.TrimEnd().Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("Series,TimestampUtc,Value,Unit", lines[0]);
        Assert.StartsWith("Tank.Level,", lines[1]);
        Assert.Contains("12.5,m", lines[1]);
        Assert.Contains("13.25,m", lines[2]);
    }

    [Fact]
    public void Build_EscapesCommasAndQuotes()
    {
        TrendSample[] samples = { new(new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc), 1) };
        var series = new[] { new TrendSeries("Tank, 1 \"A\"", "m", "Continuous", "#4FC3F7", samples) };

        string csv = TrendCsv.Build(series);

        Assert.Contains("\"Tank, 1 \"\"A\"\"\"", csv);
    }

    [Fact]
    public void Build_UsesInvariantNumberFormatting()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            TrendSample[] samples = { new(new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc), 12.5) };
            var series = new[] { new TrendSeries("Tank", "m", "Continuous", "#4FC3F7", samples) };
            string csv = TrendCsv.Build(series);
            Assert.Contains("12.5", csv);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}