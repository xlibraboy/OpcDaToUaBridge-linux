namespace OpcBridge.Core;

public sealed class InfluxOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "http://localhost:8086";
    public string Org { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string? Token { get; set; }
    public string Measurement { get; set; } = "opc_tags";
    public int TimeoutMs { get; set; } = 5000;
    public bool VerifySsl { get; set; } = true;
}
