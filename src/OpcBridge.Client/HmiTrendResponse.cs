namespace OpcBridge.Client;

public sealed class HmiTrendResponse
{
    public string SourceId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public IReadOnlyList<HmiTrendPoint> Points { get; set; } = Array.Empty<HmiTrendPoint>();
    public bool Truncated { get; set; }
    public string? Error { get; set; }
}
