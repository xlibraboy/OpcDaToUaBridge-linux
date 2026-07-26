namespace OpcBridge.App;

public sealed record DaServerConfigRequest(
    string SourceId,
    string? DisplayName,
    string? SourceType = null,
    string ProgId = "",
    string Host = "localhost",
    string? RemoteUsername = null,
    string? RemotePassword = null,
    string? RemoteDomain = null,
    string? EndpointUrl = null,
    string? SecurityMode = null,
    string? SecurityPolicy = null,
    string? UaUsername = null,
    string? UaPassword = null,
    int SessionTimeoutMs = 0,
    int ReconnectDelayMs = 0,
    int MaxMappedTags = 0,
    bool? UseSubscriptions = null,
    int UpdateRateMs = 0);
