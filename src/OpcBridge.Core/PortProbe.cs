namespace OpcBridge.Core;

/// <summary>
/// Which address families a TCP port was free on when it was probed.
/// <para>
/// The bridge listens on IPv4 only (<c>0.0.0.0</c>), so <see cref="Ipv4Free"/> is what decides
/// whether it can bind. <see cref="Ipv6Free"/> is reported, not acted on: a port held on the
/// IPv6 side only — the OPC UA Local Discovery Server's <c>[::]:4840</c> being the case that
/// prompted this — still lets the bridge bind, but a client on this machine resolving
/// <c>localhost</c> to <c>::1</c> reaches that other listener instead.
/// </para>
/// </summary>
/// <param name="Ipv4Free">True when the port could be bound on <c>0.0.0.0</c>.</param>
/// <param name="Ipv6Free">
/// True/false when IPv6 could be probed, <c>null</c> when the host has no IPv6 stack at all
/// (not a conflict — an unsupported family must not read as "busy").
/// </param>
public readonly record struct PortProbe(bool Ipv4Free, bool? Ipv6Free)
{
    /// <summary>
    /// The families another process holds, e.g. <c>"IPv6"</c>, or null when the port was free
    /// everywhere it could be probed.
    /// </summary>
    public string? HeldFamilies()
    {
        if (Ipv4Free && Ipv6Free != false)
        {
            return null;
        }

        if (!Ipv4Free && Ipv6Free == false)
        {
            return "IPv4 and IPv6";
        }

        return Ipv4Free ? "IPv6" : "IPv4";
    }
}
