using System.Text.Json.Serialization;

namespace OpcBridge.Hmi.Core;

/// <summary>
/// A saved trend group: a name plus an ordered pen list. Each pen carries the display state
/// the operator set up in the trend window (colour, visibility, Y axis), so reopening the
/// group restores the same chart. Order also fixes the strip stack and the palette colors.
/// </summary>
public sealed class TrendGroupDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<TrendGroupPenDefinition> Pens { get; set; } = new();

    /// <summary>"Mixed" (one overlay plot with a scale legend) or "Stacked" (one strip per pen).</summary>
    public string LayoutMode { get; set; } = TrendGroupLayouts.Mixed;

    /// <summary>"Shared" (one Y axis) or "Percent" (each pen scaled to its own span).</summary>
    public string YAxisMode { get; set; } = TrendGroupAxisModes.Shared;

    public IEnumerable<TagBindingKey> Keys() => Pens.Select(pen => pen.Key);
}

/// <summary>One pen of a saved trend group: the tag identity plus its saved display state.</summary>
public sealed class TrendGroupPenDefinition
{
    public string BridgeId { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public string DaItemId { get; set; } = string.Empty;

    /// <summary>Line colour as "#RRGGBB"; empty = palette colour for the pen's position.</summary>
    public string Color { get; set; } = string.Empty;

    public bool Visible { get; set; } = true;

    /// <summary>True while the pen auto-fits its samples; false pins the axis to the tag/custom range.</summary>
    public bool AxisAutoRange { get; set; } = true;

    /// <summary>Operator-typed Y axis minimum; null = unset.</summary>
    public double? RangeMin { get; set; }

    /// <summary>Operator-typed Y axis maximum; null = unset.</summary>
    public double? RangeMax { get; set; }

    [JsonIgnore]
    public TagBindingKey Key => TagBindingKey.Create(BridgeId, SourceId, DaItemId);
}

/// <summary>Layout choices a saved group can restore.</summary>
public static class TrendGroupLayouts
{
    public const string Mixed = "Mixed";
    public const string Stacked = "Stacked";

    public static string Normalize(string? value) =>
        string.Equals(value, Stacked, StringComparison.OrdinalIgnoreCase) ? Stacked : Mixed;
}

/// <summary>Y axis choices a saved group can restore.</summary>
public static class TrendGroupAxisModes
{
    public const string Shared = "Shared";
    public const string Percent = "Percent";

    public static string Normalize(string? value) =>
        string.Equals(value, Percent, StringComparison.OrdinalIgnoreCase) ? Percent : Shared;
}
