namespace OpcBridge.Drivers.S7;

/// <summary>
/// Runtime options for <see cref="S7200Client"/> (serial PPI).
/// </summary>
public sealed class S7200ClientOptions
{
    public string SourceId { get; init; } = "default";
    public string SerialPortName { get; init; } = "";
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public string Parity { get; init; } = "Even";
    public string StopBits { get; init; } = "One";
    public int LocalPpiAddress { get; init; } = 0;
    public int RemotePpiAddress { get; init; } = 2;
    public int TimeoutMs { get; init; } = 3000;
    public int RetryCount { get; init; } = 2;
}
