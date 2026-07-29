namespace OpcBridge.App;

/// <summary>
/// POST /api/ua/test-connection body. Provide either connection fields or a saved <see cref="SourceId"/>.
/// </summary>
public sealed record UaTestConnectionRequest(
    string? EndpointUrl = null,
    string? SecurityMode = null,
    string? SecurityPolicy = null,
    string? Username = null,
    string? Password = null,
    string? SourceId = null);

/// <summary>
/// POST /api/ua/browse body. Connection fields or <see cref="SourceId"/>; nodeId defaults to Objects (i=85).
/// </summary>
public sealed record UaBrowseRequest(
    string? EndpointUrl = null,
    string? SecurityMode = null,
    string? SecurityPolicy = null,
    string? Username = null,
    string? Password = null,
    string? SourceId = null,
    string? NodeId = null,
    int? MaxNodes = null);

/// <summary>
/// POST /api/ua/discover body. Probes an LDS or known UA server for registered/discoverable endpoints.
/// </summary>
public sealed record UaDiscoverRequest(
    string? EndpointUrl = null,
    string? SecurityMode = null,
    string? SecurityPolicy = null,
    string? Username = null,
    string? Password = null,
    string? SourceId = null);
