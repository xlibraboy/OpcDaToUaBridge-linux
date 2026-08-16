namespace OpcBridge.Da;

/// <summary>
/// Identity of a connected OPC DA server: the OPC DA spec level it supports
/// (1.0 / 2.0 / 3.0, probed via the async interfaces it exposes) plus the
/// server's own version, vendor string and state reported by
/// <c>IOPCServer.GetStatus</c>. Detection is best-effort: a server that fails
/// <c>GetStatus</c> still yields spec-level info with zeroed version fields.
/// </summary>
public sealed record OpcDaServerInfo(
    string SpecVersion,
    uint MajorVersion,
    uint MinorVersion,
    uint BuildNumber,
    string? VendorInfo,
    string State)
{
    /// <summary>
    /// Compact one-line summary for status surfaces, e.g.
    /// <c>OPC DA 3.0 · v2.0.1 · MatrikonOPC Simulation Server</c>.
    /// </summary>
    public string Describe()
    {
        string version = MajorVersion == 0 && MinorVersion == 0 && BuildNumber == 0
            ? string.Empty
            : BuildNumber == 0
                ? $" · v{MajorVersion}.{MinorVersion}"
                : $" · v{MajorVersion}.{MinorVersion}.{BuildNumber}";

        string? vendor = string.IsNullOrWhiteSpace(VendorInfo) ? null : VendorInfo.Trim();

        string state = string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase)
            || string.Equals(State, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" · {State}";

        return $"OPC DA {SpecVersion}{version}{state}{(vendor is null ? string.Empty : $" · {vendor}")}";
    }

    /// <summary>Maps the OPC DA server state (dwState) to a readable label.</summary>
    internal static string DescribeState(uint state)
    {
        return state switch
        {
            1 => "Running",
            2 => "Failed",
            3 => "NoConfig",
            4 => "Suspended",
            5 => "Test",
            6 => "CommFault",
            _ => "Unknown"
        };
    }
}
