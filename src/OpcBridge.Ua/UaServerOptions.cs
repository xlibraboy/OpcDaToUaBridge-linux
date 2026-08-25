namespace OpcBridge.Ua;

public sealed class UaServerOptions
{
    public string ApplicationName { get; set; } = "OpcBridge";
    public string EndpointUrl { get; set; } = "opc.tcp://0.0.0.0:4840/OpcBridge";
    public bool AutoAcceptUntrustedCertificates { get; set; } = true;
    public bool RequireAuthentication { get; set; } = false;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public List<string> AllowedIpAddresses { get; set; } = new();
}
