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
    /// Checks whether a TCP port is available (not in use).
    /// </summary>
    public static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
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
