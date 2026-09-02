namespace OpcBridge.App;

/// <summary>POST /api/influx/probe body. Host may be "192.168.1.50", "192.168.1.50:8087", or a full URL.</summary>
public sealed record InfluxProbeRequest(string? Host = null);
