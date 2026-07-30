namespace OpcBridge.Drivers.S7.Protocol;

/// <summary>
/// Thrown when a PPI frame fails BCC/framing checks or an S7 PDU reports an error.
/// </summary>
public sealed class PpiException : Exception
{
    public PpiException(string message)
        : base(message)
    {
    }

    public PpiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public int? ErrorCode { get; init; }
}
