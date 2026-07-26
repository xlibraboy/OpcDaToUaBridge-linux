using OpcBridge.Core;
using OpcBridge.Da;
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

        return new OpcDaClient(source.ToOptions(settings.UseSubscriptions));
    }
}
