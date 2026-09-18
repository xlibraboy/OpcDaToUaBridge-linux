using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>
/// Maps a pen's display state to and from its <see cref="TrendGroupPenDefinition"/>, so a saved
/// trend group reopens with the colours, Y ranges and hidden pens it was closed with.
/// The colour is the exception: it travels through the <see cref="TrendPenViewModel"/> constructor
/// (via <see cref="TrendPenViewModel.HasExplicitColor"/>) so the owning trend knows the positional
/// palette colour must not overwrite it.
/// </summary>
public static class TrendGroupPenState
{
    /// <summary>Applies the saved state to a pen built for that tag.</summary>
    public static void Apply(TrendPenViewModel pen, TrendGroupPenDefinition saved)
    {
        pen.IsVisible = saved.Visible;
        pen.AxisAutoRange = saved.AxisAutoRange;
        pen.SetCustomRange(saved.RangeMin, saved.RangeMax);
    }

    /// <summary>Copies what the operator set in the trend window back onto the saved definition.</summary>
    public static void Capture(TrendPenViewModel pen, TrendGroupPenDefinition saved)
    {
        saved.Color = pen.Color;
        saved.Visible = pen.IsVisible;
        saved.AxisAutoRange = pen.AxisAutoRange;
        saved.RangeMin = pen.CustomRangeMin;
        saved.RangeMax = pen.CustomRangeMax;
    }
}
