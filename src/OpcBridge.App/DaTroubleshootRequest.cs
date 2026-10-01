namespace OpcBridge.App;

/// <summary>
/// Request for POST /api/da/troubleshoot. Either a configured <paramref name="SourceId"/>
/// (config and credentials taken from sources.json) or ad-hoc ProgID/host/credentials.
/// <paramref name="IncludeProbe"/> additionally runs the isolated activation probe.
/// </summary>
public sealed record DaTroubleshootRequest(
    string? SourceId = null,
    string? ProgId = null,
    string? Host = null,
    string? Username = null,
    string? Password = null,
    string? Domain = null,
    bool IncludeProbe = false);
