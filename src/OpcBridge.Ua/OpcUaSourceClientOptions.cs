namespace OpcBridge.Ua;

public sealed class OpcUaSourceClientOptions
{
    public string SourceId { get; set; } = "default";
    public string DisplayName { get; set; } = "";
    public string EndpointUrl { get; set; } = "";
    public string SecurityMode { get; set; } = "None"; // None|Sign|SignAndEncrypt
    public string SecurityPolicy { get; set; } = "None"; // None|Basic256Sha256
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int UpdateRateMs { get; set; } = 1000;
    public int SessionTimeoutMs { get; set; } = 60000;
    public int ReconnectDelayMs { get; set; } = 5000;
    public bool UseSubscriptions { get; set; } = true;
    public string ApplicationName { get; set; } = "OpcDaToUaBridge.UaClient";
    public string PkiRoot { get; set; } = "pki/ua-client"; // under BaseDirectory
    public bool AutoAcceptUntrustedCertificates { get; set; } = true; // lab default
}
