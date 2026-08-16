namespace OpcBridge.Core;

public sealed class BridgeOptions
{
    public int HttpPort { get; set; } = 8080;
    public int OpcUaPort { get; set; } = 4840;
    public List<TagMapping> Mappings { get; set; } = new();
    public Dictionary<int, int> RateLimits { get; set; } = new();
    public int ExpectedTagCount { get; set; } = 1000;
}