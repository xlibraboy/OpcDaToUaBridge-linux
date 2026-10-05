using System.Globalization;

namespace OpcBridge.Hmi.Core;

/// <summary>
/// The one display precision for trend numbers. The pen-table readouts, the chart's hover/pin
/// value chips and its Y-axis labels all format through here, so a value read one way matches
/// the other. Bounding the digits also hides binary widening: the bridge rounds a tag to a few
/// decimals as a float, and InfluxDB stores that widened to float64 (12.34 becomes
/// 12.340000152587891), so a trend echoing the raw double would show that noise instead of the
/// number the operator configured.
/// </summary>
public static class TrendNumberFormat
{
    /// <summary>Digits kept after the decimal point; trailing zeros are trimmed.</summary>
    public const int Decimals = 3;

    /// <summary>
    /// Formats a trend value: rounded to <see cref="Decimals"/> with trailing zeros trimmed
    /// ("12.34", "12.3", "12", "0").
    /// </summary>
    public static string Format(double value)
    {
        double rounded = Math.Round(value, Decimals);
        return rounded == 0 ? "0" : rounded.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
