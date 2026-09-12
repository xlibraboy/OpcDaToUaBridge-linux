namespace OpcBridge.Client;

public sealed class HmiTagDto
{
    public string SourceId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Tag description shown in the trend pen table (from the mapping).</summary>
    public string? Description { get; set; }

    public string DataType { get; set; } = "Double";
    public object? Value { get; set; }
    public DateTime? TimestampUtc { get; set; }
    public int? DaQuality { get; set; }
    public bool? IsGood { get; set; }
    public bool Writeable { get; set; }

    /// <summary>Effective update rate in ms (per-tag override, else the source default). 0 = unknown.</summary>
    public int UpdateRateMs { get; set; }

    /// <summary>Engineering unit label (e.g. "°C", "bar"). Set per-tag in the dashboard.</summary>
    public string? Unit { get; set; }

    /// <summary>
    /// How this tag's history renders in HMI trend charts: "Continuous" (line through the
    /// samples, default) or "Step" (sample-and-hold). Set per-tag in the dashboard Maps faceplate.
    /// </summary>
    public string TrendStyle { get; set; } = "Continuous";

    /// <summary>
    /// Whether this tag's values are written to InfluxDB (the "Influx log" checkbox on the
    /// dashboard Maps faceplate). Only tags with history enabled can display trends.
    /// </summary>
    public bool InfluxEnabled { get; set; }

    /// <summary>
    /// True when the faceplate should render this tag as a two-state on/off signal.
    /// Resolved from the mapping: Boolean tags are digital automatically, others only when
    /// explicitly marked (a Byte tag carrying 0/1).
    /// </summary>
    public bool Digital { get; set; }

    /// <summary>Faceplate label for the on state. null/blank = render the raw value.</summary>
    public string? OnText { get; set; }

    /// <summary>Faceplate label for the off state. null/blank = render the raw value.</summary>
    public string? OffText { get; set; }
}
