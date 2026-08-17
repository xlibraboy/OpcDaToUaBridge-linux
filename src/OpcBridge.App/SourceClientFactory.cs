using Microsoft.Extensions.Logging;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Drivers.Melsec;
using OpcBridge.Drivers.MxComponent;
using OpcBridge.Drivers.S7;
using OpcBridge.Ua;

namespace OpcBridge.App;

public class SourceClientFactory
{
    private readonly ILoggerFactory? logger_factory_;

    public SourceClientFactory(ILoggerFactory? loggerFactory = null)
    {
        logger_factory_ = loggerFactory;
    }

    public virtual ISourceClient Create(DaRuntimeSettingsSnapshot settings, DaSourceRuntimeSettings source)
    {
        if (string.Equals(source.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
        {
            return new OpcUaSourceClient(
                source.ToUaOptions(settings),
                logger_factory_ is null ? null : logger_factory_.CreateLogger<OpcUaSourceClient>());
        }

        if (string.Equals(source.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
        {
            return new MelsecA3nClient(ToMelsecOptions(source));
        }

        if (string.Equals(source.SourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
        {
            return new S7200Client(ToS7200Options(source));
        }

        if (string.Equals(source.SourceType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
        {
            return new MxComponentClient(ToMxComponentOptions(source));
        }

        // Per-source client I/O mode decides whether this DA source attempts the push
        // path: Sync never does, AutoDetect follows the global master switch, and
        // Async20 forces the attempt regardless of the global switch.
        bool attemptSubscriptions = ResolveSubscriptionAttempt(source.IoMode, settings.UseSubscriptions);
        return new OpcDaClient(source.ToOptions(attemptSubscriptions, source.IoMode));
    }

    private static bool ResolveSubscriptionAttempt(string ioMode, bool globalUseSubscriptions)
    {
        if (string.Equals(ioMode, "Sync", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(ioMode, "Async20", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return globalUseSubscriptions;
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

    private static MxComponentClientOptions ToMxComponentOptions(DaSourceRuntimeSettings source) => new()
    {
        SourceId = source.SourceId,
        LogicalStationNumber = source.MxComponent?.LogicalStationNumber ?? 0,
        TimeoutMs = source.MxComponent?.TimeoutMs ?? 3000,
        RetryCount = source.MxComponent?.RetryCount ?? 2
    };
}
