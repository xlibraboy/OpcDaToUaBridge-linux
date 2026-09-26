using System.Net;
using System.Net.Sockets;

namespace OpcBridge.Core;

/// <summary>
/// Utilities for dynamic port allocation.
/// </summary>
public static class PortHelper
{
    private const int DefaultHttpPort = 8080;
    private const int DefaultOpcUaPort = 4840;

    /// <summary>
    /// Checks whether a TCP port can be bound on IPv4 (0.0.0.0), which is the criterion for
    /// whether the bridge can listen on it. A port that another process holds on the IPv6
    /// side only is still bindable here — that case is reported by <see cref="Probe"/>
    /// rather than treated as unavailable, because moving the bridge would invalidate the
    /// installer's build-time firewall rule.
    /// </summary>
    public static bool IsPortAvailable(int port)
    {
        return Probe(port).Ipv4Free;
    }

    /// <summary>
    /// Probes a port on each address family separately. Run this before the bridge binds:
    /// afterwards its own listener makes the port read as taken.
    /// </summary>
    public static PortProbe Probe(int port)
    {
        return new PortProbe(IsIpv4Free(port), IsIpv6Free(port));
    }

    private static bool IsIpv4Free(int port)
    {
        // IPv4 is always probeable, so the only possible null is the IPv6 side.
        return TryBind(IPAddress.Any, port) == true;
    }

    private static bool? IsIpv6Free(int port)
    {
        return TryBind(IPAddress.IPv6Any, port);
    }

    /// <summary>
    /// True when the port could be bound, false when it is taken, null when this host has no
    /// such address family.
    /// </summary>
    private static bool? TryBind(IPAddress address, int port)
    {
        bool ipv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        try
        {
            using var listener = new TcpListener(address, port);
            if (ipv6)
            {
                // Probe IPv6 on its own. A dual-mode socket would also try to claim IPv4,
                // which would report IPv6 busy whenever IPv4 is.
                listener.Server.DualMode = false;
            }

            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException exception) when (ipv6 && IsFamilyUnsupported(exception.SocketErrorCode))
        {
            // No IPv6 stack on this host: unknown, never "busy".
            return null;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsFamilyUnsupported(SocketError error)
    {
        return error == SocketError.AddressFamilyNotSupported
            || error == SocketError.ProtocolNotSupported
            || error == SocketError.OperationNotSupported;
    }

    /// <summary>
    /// Scans upward from <paramref name="start"/> to <paramref name="end"/> inclusive,
    /// returning the first port that is available.
    /// Returns -1 if none are available.
    /// </summary>
    public static int FindAvailablePort(int start, int end)
    {
        for (int port = start; port <= end; port++)
        {
            if (IsPortAvailable(port))
                return port;
        }
        return -1;
    }

    /// <summary>
    /// Returns true if the current HTTP port differs from the default (8080),
    /// meaning it was auto-assigned.
    /// </summary>
    public static bool IsHttpAutoAssigned(int current) => current != DefaultHttpPort;

    /// <summary>
    /// Returns true if the current UA port differs from the default (4840),
    /// meaning it was auto-assigned.
    /// </summary>
    public static bool IsOpcUaAutoAssigned(int current) => current != DefaultOpcUaPort;

    public const int HttpScanStart = 8080;
    public const int HttpScanEnd = 8180;
    public const int OpcUaScanStart = 4840;
    public const int OpcUaScanEnd = 4940;
}
