namespace OpcBridge.Da;

/// <summary>
/// Thrown when an OPC DA connection is lost at the COM/RPC level — server unreachable,
/// DCOM channel disconnected, or group/item management fails mid-session. The bridge
/// treats this as transient: the source is torn down and reconnected with backoff.
/// Configuration errors (bad ProgID, logon failure, unknown item IDs) keep using
/// <see cref="InvalidOperationException"/> and are reported as Faulted without retry.
/// </summary>
public sealed class SourceConnectionLostException : Exception
{
    public SourceConnectionLostException(string message)
        : base(message)
    {
    }

    public SourceConnectionLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
