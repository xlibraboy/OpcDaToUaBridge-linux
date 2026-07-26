namespace OpcBridge.Drivers.Melsec.Protocol;

/// <summary>
/// Thrown when a MELSEC 1C Frame response is NAK, fails sum-check, or is otherwise invalid.
/// </summary>
public sealed class MelsecProtocolException : Exception
{
    public MelsecProtocolException(string message)
        : base(message)
    {
    }

    public MelsecProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
