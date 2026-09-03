namespace OpcBridge.Hmi.Core;

/// <summary>
/// A numeric, timestamped sample that makes up a trend series.
/// Non-numeric samples are filtered out before this point.
/// </summary>
public readonly record struct TrendSample(DateTime T, double V);

/// <summary>
/// Pre-computed value (Y) axis layout for a trend chart.
/// </summary>
public readonly record struct TrendAxis(double Min, double Max, double Step)
{
    public bool IsValid =>
        Step > 0
        && Max > Min
        && double.IsFinite(Min)
        && double.IsFinite(Max)
        && double.IsFinite(Step);
}

/// <summary>
/// Resolves the "nice" min/max range and grid step a standard SCADA trend
/// should draw for a tag. Pure logic so the axis policy is unit-testable
/// without a UI.
/// </summary>
public static class TrendScale
{
    /// <summary>Target number of intervals between horizontal gridlines.</summary>
    private const int TargetIntervals = 5;

    /// <summary>
    /// Pick the Y axis for a series.
    /// </summary>
    /// <param name="autoRange">
    /// When true the axis is fitted to the samples with a small margin and rounded to nice
    /// tick values. When false the axis is pinned to the tag's data-type range (min..max).
    /// </param>
    /// <param name="typeRange">
    /// The tag's natural range (e.g. byte 0..255, bool 0..1). Null for floating types.
    /// When a fixed range is requested but none exists, the axis auto-fits.
    /// </param>
    /// <param name="dataMin">Minimum numeric sample in the window (null when none).</param>
    /// <param name="dataMax">Maximum numeric sample in the window (null when none).</param>
    public static TrendAxis Resolve(
        bool autoRange,
        (double Min, double Max)? typeRange,
        double? dataMin,
        double? dataMax)
    {
        if (dataMin is not { } loRaw
            || dataMax is not { } hiRaw
            || !double.IsFinite(loRaw)
            || !double.IsFinite(hiRaw))
        {
            return default;
        }

        double lo = Math.Min(loRaw, hiRaw);
        double hi = Math.Max(loRaw, hiRaw);
        if (IsDegenerate(lo, hi))
        {
            // Flat series: fall back to the type range when available, otherwise draw a
            // small band around the value so the trace is not glued to the edge.
            if (typeRange is { } tr)
            {
                return FromTypeRange(tr);
            }

            double band = Math.Max(Math.Abs(lo) * 0.05, 0.5);
            return NiceRange(lo - band, hi + band);
        }

        if (!autoRange && typeRange is { } fixedRange)
        {
            return FromTypeRange(fixedRange);
        }

        double pad = (hi - lo) * 0.05;
        return NiceRange(lo - pad, hi + pad);
    }

    /// <summary>
    /// Axis pinned exactly to a tag's type range, with a nice grid step inside it.
    /// </summary>
    public static TrendAxis FromTypeRange((double Min, double Max) typeRange)
    {
        double min = typeRange.Min;
        double max = typeRange.Max;
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min)
        {
            return default;
        }

        double step = NiceStep((max - min) / TargetIntervals);
        if (!(step > 0) || !double.IsFinite(step))
        {
            return default;
        }

        return new TrendAxis(min, max, step);
    }

    /// <summary>True when min/max are equal to within floating-point noise.</summary>
    private static bool IsDegenerate(double lo, double hi)
    {
        double scale = Math.Max(1.0, Math.Max(Math.Abs(lo), Math.Abs(hi)));
        return hi - lo <= scale * 1e-9;
    }

    /// <summary>
    /// Fits [lo, hi] to gridlines on "nice" numbers (…, 10, 20, 50, 100, … spacing).
    /// </summary>
    private static TrendAxis NiceRange(double lo, double hi)
    {
        double span = hi - lo;
        if (!(span > 0) || !double.IsFinite(span))
        {
            return default;
        }

        double step = NiceStep(span / TargetIntervals);
        if (!(step > 0) || !double.IsFinite(step))
        {
            return default;
        }

        double min = Math.Floor(lo / step) * step;
        double max = Math.Ceiling(hi / step) * step;
        if (max <= min)
        {
            max = min + step;
        }

        double count = Math.Round((max - min) / step);
        if (count < 1)
        {
            count = 1;
        }

        if (count > TargetIntervals * 2 + 2)
        {
            // Padding pushed the floored/ceiled bounds far apart (tiny step vs large span);
            // step the range up again rather than emitting a wall of gridlines.
            step = NiceStep(span / (TargetIntervals - 1));
            min = Math.Floor(lo / step) * step;
            max = Math.Ceiling(hi / step) * step;
            if (max <= min)
            {
                max = min + step;
            }
        }

        return new TrendAxis(min, max, step);
    }

    /// <summary>Rounds a raw step up to 1/2/5 * 10^n, the classic "pretty" numbers.</summary>
    private static double NiceStep(double raw)
    {
        if (!(raw > 0) || !double.IsFinite(raw))
        {
            return 0;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double normalized = raw / magnitude;
        double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }
}
