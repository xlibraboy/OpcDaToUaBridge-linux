using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>One bridge the Logic page can show: its id, live link state and base URL.</summary>
public sealed record LogicBridgeOption(string BridgeId, BridgeLinkState State, string BaseUrl = "")
{
    public string Label => BridgeId;

    public bool IsConnected => State == BridgeLinkState.Connected;

    /// <summary>The bridge's own logic page. Empty while its address is unknown.</summary>
    public string PageUrl => string.IsNullOrWhiteSpace(BaseUrl)
        ? string.Empty
        : BaseUrl.TrimEnd('/') + "/logic";

    public string StatusText => State switch
    {
        BridgeLinkState.Connected => "Connected",
        BridgeLinkState.Reconnecting => "Reconnecting",
        _ => "Failed"
    };

    public string StatusKey => State switch
    {
        BridgeLinkState.Connected => "ok",
        BridgeLinkState.Reconnecting => "warn",
        _ => "bad"
    };
}
