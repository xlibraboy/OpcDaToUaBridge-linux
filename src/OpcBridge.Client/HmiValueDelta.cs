namespace OpcBridge.Client;

public sealed class HmiValueDelta
{
    public string SourceId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public object? Value { get; set; }
    public DateTime TimestampUtc { get; set; }
    public int DaQuality { get; set; }
    public bool IsGood { get; set; }
}
