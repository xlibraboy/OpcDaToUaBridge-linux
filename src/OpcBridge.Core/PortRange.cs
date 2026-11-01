using System.Globalization;
using System.Text.Json.Serialization;

namespace OpcBridge.Core;

/// <summary>
/// A contiguous block of TCP ports. An install owns one HTTP block and one OPC UA block
/// (<c>Bridge:HttpPortRange</c> / <c>Bridge:OpcUaPortRange</c>), and the bridge only
/// auto-assigns inside them — so two installs on one machine, or an install next to another
/// product, cannot take each other's numbers when a preferred port is busy.
///
/// The config form is the string <c>"start-end"</c>; the API form is the default
/// <c>{"start":…,"end":…}</c> of this record.
/// </summary>
public readonly record struct PortRange(int Start, int End)
{
    public static PortRange DefaultHttp => new(PortHelper.HttpScanStart, PortHelper.HttpScanEnd);

    public static PortRange DefaultUa => new(PortHelper.OpcUaScanStart, PortHelper.OpcUaScanEnd);

    [JsonIgnore]
    public bool IsValid => Start >= 1 && End <= 65535 && Start <= End;

    [JsonIgnore]
    public int Length => IsValid ? End - Start + 1 : 0;

    public bool Contains(int port) => IsValid && port >= Start && port <= End;

    public bool Overlaps(PortRange other) =>
        IsValid && other.IsValid && Start <= other.End && other.Start <= End;

    /// <summary>Parses the config form <c>"start-end"</c>; whitespace around the parts is allowed.</summary>
    public static bool TryParse(string? text, out PortRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('-');
        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int start)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int end))
        {
            return false;
        }

        PortRange candidate = new(start, end);
        if (!candidate.IsValid)
        {
            return false;
        }

        range = candidate;
        return true;
    }

    public override string ToString() => $"{Start}-{End}";
}
