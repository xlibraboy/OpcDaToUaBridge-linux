namespace OpcBridge.Client;

public sealed class HmiTrendPoint
{
    public DateTime T { get; set; }
    public object? V { get; set; }
    public int? Q { get; set; }
    public bool? Good { get; set; }
}
