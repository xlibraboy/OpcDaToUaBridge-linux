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
    string? Transport = null,
    string? SerialPortName = null,
    int BaudRate = 0,
    int DataBits = 0,
    string? Parity = null,
    string? StopBits = null,
    string? StationNo = null,
    string? PcNo = null,
    int LocalPpiAddress = 0,
    int RemotePpiAddress = 2,
    int TimeoutMs = 0,
    int RetryCount = -1,
    string? EndpointUrl = null,
    string? SecurityMode = null,
    string? SecurityPolicy = null,
    string? UaUsername = null,
    string? UaPassword = null,
    int SessionTimeoutMs = 0,
    int ReconnectDelayMs = 0,
    int MaxMappedTags = 0,
    bool? UseSubscriptions = null,
    int UpdateRateMs = 0,
    int? WatchdogTimeoutMs = null);

public sealed record MelsecTestConnectionRequest(
    string? SourceId = null,
    string? SerialPortName = null,
    int? BaudRate = null,
    int? DataBits = null,
    string? Parity = null,
    string? StopBits = null,
    string? StationNo = null,
    string? PcNo = null,
    int? TimeoutMs = null,
    int? RetryCount = null);

public sealed record MelsecParseAddressRequest(string Address);

public sealed record S7200TestConnectionRequest(
    string? SourceId = null,
    string? SerialPortName = null,
    int? BaudRate = null,
    int? DataBits = null,
    string? Parity = null,
    string? StopBits = null,
    int? LocalPpiAddress = null,
    int? RemotePpiAddress = null,
    int? TimeoutMs = null,
    int? RetryCount = null);

public sealed record S7200ParseAddressRequest(string Address);
