namespace OpcBridge.Client;

public sealed class HmiTagDto
{
    public string SourceId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
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
}
