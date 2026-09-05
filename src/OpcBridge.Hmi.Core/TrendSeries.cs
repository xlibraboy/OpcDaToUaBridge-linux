using System.Text;
using System.Globalization;

namespace OpcBridge.Hmi.Core;

/// <summary>
/// One plottable trace ("pen") in a trend chart: its rendering settings (name,
/// description, unit, line style, color, visibility) plus the numeric samples loaded
/// for the current window. A single-tag trend has exactly one pen; a group trend has
/// several, each drawn in its own strip. <see cref="IsBoolean"/> marks discrete on/off
/// signals, which render as square-wave strips with optional
/// <see cref="StateLabels"/> (index 0 = false state text, 1 = true state text).
/// </summary>
public readonly record struct TrendSeries(
    string Name,
    string Unit,
    string TrendStyle,
    string Color,
    IReadOnlyList<TrendSample> Samples,
    string Description = "",
    bool Visible = true,
    bool IsBoolean = false,
    (double Low, double High)? AlarmLimits = null,
    string?[]? StateLabels = null,
    bool UsePercentAxis = false,
    (double Min, double Max, double Step)? FixedAxis = null);

/// <summary>
/// Stable per-trace colors used across the HMI trend charts. Single-tag trends always
/// use the first color; group trends assign palette colors in tag order.
/// </summary>
public static class TrendSeriesPalette
{
    public static readonly string[] Colors =
    {
        "#4FC3F7", // light blue (single-tag default)
        "#F48FB1", // pink
        "#A5D6A7", // green
        "#FFE082", // amber
        "#CE93D8", // purple
        "#80CBC4", // teal
        "#EF9A9A", // salmon
        "#90CAF9", // pale blue
        "#FFF59D", // pale yellow
        "#A1887F"  // brown
    };

    /// <summary>Palette color for the series at <paramref name="index"/>, cycling safely.</summary>
    public static string ColorFor(int index) => Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];
}

/// <summary>
/// Parses the trend's range labels ("15m", "1h", "8h", "24h", or a plain hour count)
/// into hours. Pure logic so the range buttons are unit-testable without a UI.
/// </summary>
public static class TrendRange
{
    /// <summary>Parses a label into hours; unknown/empty input falls back to 1h.</summary>
    public static double ParseHours(string? label)
    {
        string text = (label ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return 1;
        }

        string digits = text;
        double multiplier = 1;
        char? suffix = char.ToLowerInvariant(text[^1]);
        if (suffix is 'm' or 'h')
        {
            digits = text[..^1];
            multiplier = suffix == 'm' ? 1.0 / 60.0 : 1;
        }

        if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0)
        {
            return value * multiplier;
        }

        return 1;
    }

    /// <summary>Formats hours as the canonical label ("0.25" → "15m", "1" → "1h").</summary>
    public static string Normalize(double hours)
    {
        if (!(hours > 0) || !double.IsFinite(hours))
        {
            return "1h";
        }

        if (hours < 1)
        {
            double minutes = Math.Round(hours * 60);
            return minutes < 1 ? "1m" : minutes.ToString("0", CultureInfo.InvariantCulture) + "m";
        }

        if (hours == Math.Truncate(hours))
        {
            return hours.ToString("0", CultureInfo.InvariantCulture) + "h";
        }

        return hours.ToString("0.##", CultureInfo.InvariantCulture) + "h";
    }
}

/// <summary>
/// Windowed aggregate statistics for one pen, matching the columns of the pen
/// configuration table (value, minimum, maximum, average, delta). Pure logic so the
/// numbers are unit-testable without a UI.
/// </summary>
public static class TrendPenStats
{
    /// <summary>Result of aggregating a pen's samples over the displayed window.</summary>
    public readonly record struct Result(
        double Value,
        double Minimum,
        double Maximum,
        double Average,
        double Delta,
        double StdDev,
        int Count);

    /// <summary>
    /// Aggregates finite samples in [from, to]. Returns null when no usable sample
    /// exists; <see cref="Result.Value"/> is then the last finite sample overall.
    /// </summary>
    public static Result? Compute(IReadOnlyList<TrendSample> samples, DateTime from, DateTime to)
    {
        double lastOverall = double.NaN;
        double min = double.MaxValue;
        double max = double.MinValue;
        double sum = 0;
        double sumSquares = 0;
        int count = 0;

        for (int i = 0; i < samples.Count; i++)
        {
            TrendSample sample = samples[i];
            if (!double.IsFinite(sample.V))
            {
                continue;
            }

            lastOverall = sample.V;
            if (sample.T < from || sample.T > to)
            {
                continue;
            }

            min = Math.Min(min, sample.V);
            max = Math.Max(max, sample.V);
            sum += sample.V;
            sumSquares += sample.V * sample.V;
            count++;
        }

        if (count == 0)
        {
            return double.IsNaN(lastOverall)
                ? null
                : new Result(lastOverall, lastOverall, lastOverall, lastOverall, 0, 0, 0);
        }

        double avg = sum / count;
        // Population stddev; for count==1 the variance is defined as zero.
        double variance = count > 1 ? Math.Max(0, sumSquares / count - avg * avg) : 0;
        return new Result(
            lastOverall,
            min,
            max,
            avg,
            max - min,
            Math.Sqrt(variance),
            count);
    }
}
