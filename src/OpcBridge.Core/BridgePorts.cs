namespace OpcBridge.Core;

/// <summary>
/// Runtime port information exposed via GET /api/status/ports.
/// </summary>
public sealed record BridgePorts(
    int HttpPort,
    int UaPort,
    int HttpDefault,
    int UaDefault,
    PortRange HttpRange,
    PortRange UaRange,
    bool HttpAutoAssigned,
    bool UaAutoAssigned,
    string? UaEndpointBind,
    string? UaEndpointClient,
    PortProbe HttpProbe,
    PortProbe UaProbe,
    string? DiscoveryServer);
