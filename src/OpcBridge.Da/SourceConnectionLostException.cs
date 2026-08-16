namespace OpcBridge.Da;

/// <summary>
/// Thrown when a source connection is lost at the transport/session level — server
/// unreachable, channel disconnected, session invalid, or the server went away
/// mid-session. The bridge treats this as transient: the source is torn down and
/// reconnected with backoff. Configuration errors (bad endpoint format, logon
/// failure) keep using <see cref="InvalidOperationException"/> and are reported as
/// Faulted without retry.
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
