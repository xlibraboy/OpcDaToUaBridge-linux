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
    int TimeoutMs = 0,
    int RetryCount = -1,
    int MaxMappedTags = 0,
    int UpdateRateMs = 0);

public sealed record MelsecTestConnectionRequest(
    string? SourceId = null,
    string? SerialPortName = null,
    int? BaudRate = null,
    int? DataBits = null,
    string? Parity = null,
    string? StopBits = null,
    string? StationNo = null,
    string? PcNo = null,
    int? TimeoutMs = null);

public sealed record MelsecParseAddressRequest(string Address);
