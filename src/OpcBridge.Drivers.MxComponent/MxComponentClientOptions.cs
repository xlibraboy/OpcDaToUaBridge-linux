namespace OpcBridge.Drivers.MxComponent;

/// <summary>
/// Runtime options for <see cref="MxComponentClient"/> (MELSOFT MX Component 4 / ActUtlType).
/// The physical connection (serial port / Ethernet, protocol, baud, station) is configured
/// once inside MX Component's Communication Settings Utility, which assigns a
/// <see cref="LogicalStationNumber"/> — this driver only references that number.
/// </summary>
public sealed class MxComponentClientOptions
{
    public string SourceId { get; init; } = "default";
    public int LogicalStationNumber { get; init; }
    public int TimeoutMs { get; init; } = 3000;
    public int RetryCount { get; init; } = 2;
}
