namespace OpcBridge.Drivers.Melsec;

/// <summary>
/// Runtime options for <see cref="MelsecA3nClient"/> (serial 1C A3N).
/// </summary>
public sealed class MelsecA3nClientOptions
{
    public string SourceId { get; init; } = "default";
    public string SerialPortName { get; init; } = "";
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public string Parity { get; init; } = "Odd";
    public string StopBits { get; init; } = "One";
    public string StationNo { get; init; } = "00";
    public string PcNo { get; init; } = "FF";
    public int TimeoutMs { get; init; } = 3000;
    public int RetryCount { get; init; } = 2;
}
