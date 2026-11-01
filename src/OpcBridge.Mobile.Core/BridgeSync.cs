namespace OpcBridge.Mobile.Core;

/// <summary>
/// How far one bridge's phone data has moved from the bridge: the value shown beside the
/// connection status. <see cref="Syncing"/> is a read/flush in progress, <see cref="Offline"/>
/// means the link is down, <see cref="Pending"/> means the link is up but the outbox still has
/// work, and <see cref="Error"/> means the outbox has items the bridge refused.
/// </summary>
public enum BridgeSyncState
{
    Synced,
    Syncing,
    Offline,
    Pending,
    Error
}
