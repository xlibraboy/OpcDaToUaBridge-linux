namespace OpcBridge.Core;

/// <summary>
/// Runtime port information exposed via GET /api/status/ports.
/// </summary>
public sealed record BridgePorts(
    int HttpPort,
    int UaPort,
    int HttpDefault,
    int UaDefault,
    bool HttpAutoAssigned,
    bool UaAutoAssigned,
    string? UaEndpointBind,
    string? UaEndpointClient);
