namespace OpcBridge.App;

public sealed record DaServerBrowseRequest(
    string? Host = null,
    string? Username = null,
    string? Password = null,
    string? Domain = null);
