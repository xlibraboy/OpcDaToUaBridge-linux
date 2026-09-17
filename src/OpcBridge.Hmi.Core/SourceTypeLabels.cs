using OpcBridge.Core;

namespace OpcBridge.Hmi.Core;

/// <summary>
/// Short operator-facing label for a bridge source type, matching the dashboard's source
/// type badges (DA / UA / A3N / S7-200 / MX).
/// </summary>
public static class SourceTypeLabels
{
    /// <summary>Label shown when the source type is unknown (e.g. a source removed from the bridge).</summary>
    public const string Unknown = "";

    public static string ShortLabel(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return Unknown;
        }

        string type = sourceType.Trim();
        if (string.Equals(type, SourceTypes.OpcDa, StringComparison.OrdinalIgnoreCase))
        {
            return "DA";
        }

        if (string.Equals(type, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
        {
            return "UA";
        }

        if (string.Equals(type, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
        {
            return "A3N";
        }

        if (string.Equals(type, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
        {
            return "S7-200";
        }

        if (string.Equals(type, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
        {
            return "MX";
        }

        return type;
    }
}
