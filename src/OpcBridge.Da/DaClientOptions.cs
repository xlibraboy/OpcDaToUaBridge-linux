namespace OpcBridge.Da;

public sealed class DaClientOptions
{
    public bool UseSubscriptions { get; set; } = true;

    /// <summary>
    /// Client-side I/O mode for this source: "AutoDetect" (try push, fall back to
    /// polling), "Sync" (always poll via IOPCSyncIO), or "Async20" (force the
    /// IOPCDataCallback push path; falls back to polling with a loud warning when
    /// the server cannot provide it). Mirrors the per-group I/O selector in
    /// Matrikon OPC Explorer.
    /// </summary>
    public string IoMode { get; set; } = "AutoDetect";
    public string SourceId { get; set; } = "default";
    public string DisplayName { get; set; } = string.Empty;
    public string ProgId { get; set; } = string.Empty;
    public string Host { get; set; } = "localhost";
    public int UpdateRateMs { get; set; } = 1000;
    public string? RemoteUsername { get; set; }
    public string? RemotePassword { get; set; }
    public string? RemoteDomain { get; set; }
    public List<DaSourceOptions> Sources { get; set; } = new();
}

public sealed class DaSourceOptions
{
    public string SourceId { get; set; } = "default";
    public string DisplayName { get; set; } = string.Empty;
    public string ProgId { get; set; } = string.Empty;
    public string Host { get; set; } = "localhost";
    public int UpdateRateMs { get; set; } = 0;
    public string? RemoteUsername { get; set; }
    public string? RemotePassword { get; set; }
    public string? RemoteDomain { get; set; }
}