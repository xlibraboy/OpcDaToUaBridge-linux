using Microsoft.AspNetCore.SignalR;
using OpcBridge.App.Presence;

namespace OpcBridge.App.Hmi;

/// <summary>
/// The bridge's live feed for the Logic page, the HMI and the Android viewer. Every
/// connection is also registered in <see cref="ClientPresenceStore"/>, which is what the
/// dashboard's Bridge Clients card reads: who is connected, from which PC and account.
/// </summary>
public sealed class HmiHub : Hub
{
    private readonly ClientPresenceStore presence_;

    public HmiHub(ClientPresenceStore presence)
    {
        presence_ = presence;
    }

    public override Task OnConnectedAsync()
    {
        presence_.Connect(ClientPresenceReader.Read(Context.ConnectionId, Context.GetHttpContext()));
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        presence_.Disconnect(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
