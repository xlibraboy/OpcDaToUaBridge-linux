using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Drivers.Melsec;
using OpcBridge.Drivers.S7;
using OpcBridge.Ua;

namespace OpcBridge.App;

public class SourceClientFactory
{
    public virtual ISourceClient Create(DaRuntimeSettingsSnapshot settings, DaSourceRuntimeSettings source)
    {
        if (string.Equals(source.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
        {
            return new OpcUaSourceClient(source.ToUaOptions(settings));
        }

        if (string.Equals(source.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
        {
            return new MelsecA3nClient(ToMelsecOptions(source));
        }

        if (string.Equals(source.SourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
        {
            return new S7200Client(ToS7200Options(source));
        }

        return new OpcDaClient(source.ToOptions(settings.UseSubscriptions));
    }

    private static MelsecA3nClientOptions ToMelsecOptions(DaSourceRuntimeSettings source) => new()
    {
        SourceId = source.SourceId,
        SerialPortName = source.SerialPortName,
        BaudRate = source.BaudRate,
        DataBits = source.DataBits,
        Parity = source.Parity,
        StopBits = source.StopBits,
        StationNo = source.StationNo,
        PcNo = source.PcNo,
        TimeoutMs = source.TimeoutMs,
        RetryCount = source.RetryCount
    };

    private static S7200ClientOptions ToS7200Options(DaSourceRuntimeSettings source) => new()
    {
        SourceId = source.SourceId,
        SerialPortName = source.S7200?.SerialPortName ?? source.SerialPortName,
        BaudRate = source.S7200?.BaudRate ?? source.BaudRate,
        DataBits = source.S7200?.DataBits ?? source.DataBits,
        Parity = source.S7200?.Parity ?? source.Parity,
        StopBits = source.S7200?.StopBits ?? source.StopBits,
        LocalPpiAddress = source.LocalPpiAddress,
        RemotePpiAddress = source.RemotePpiAddress,
        TimeoutMs = source.S7200?.TimeoutMs ?? source.TimeoutMs,
        RetryCount = source.S7200?.RetryCount ?? source.RetryCount
    };
}
