using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Drivers.Melsec;
using OpcBridge.Ua;

namespace OpcBridge.App;

public sealed class DaClientFactory
{
    public IDaClient Create(DaRuntimeSettingsSnapshot settings, DaSourceRuntimeSettings source)
    {
        if (string.Equals(source.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
        {
            return new OpcUaSourceClient(source.ToUaOptions(settings));
        }

        if (string.Equals(source.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
        {
            return new MelsecA3nClient(ToMelsecOptions(source));
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
}
