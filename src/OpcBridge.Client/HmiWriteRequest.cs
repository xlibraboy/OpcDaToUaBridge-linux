namespace OpcBridge.Client;

public sealed class HmiWriteRequest
{
    public string SourceId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public object? Value { get; set; }
}
