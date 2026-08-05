namespace OpcBridge.Da;

/// <summary>
/// Implemented by source clients that can run in subscription mode where
/// <see cref="ISourceClient.ReadAsync"/> performs no device reads (values arrive via
/// callbacks instead). The bridge watchdog uses this to decide whether callback
/// staleness indicates a lost connection.
/// </summary>
public interface ISubscriptionActiveSource
{
    bool IsSubscriptionActive { get; }
}
