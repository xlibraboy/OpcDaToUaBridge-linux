namespace OpcBridge.App;

public sealed record DaTagBrowseRequest(
    string ProgId,
    string Host,
    string? Path = null,
    string? RemoteUsername = null,
    string? RemotePassword = null,
    string? RemoteDomain   = null);
