using System.Net;
using System.Net.Sockets;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// <see cref="PortHelper.Probe"/> separates the address families because the bridge listens on
/// IPv4 only: a process holding just the IPv6 side — the OPC UA Local Discovery Server's
/// [::]:4840 is the case that prompted this — is a conflict to *report*, not a reason to move
/// ports. <see cref="PortHelper.IsPortAvailable"/> deliberately keeps its IPv4 criterion.
/// </summary>
public sealed class PortHelperTests
{
    [Fact]
    public void Probe_ReportsBothFamiliesFree_ForAnUnusedPort()
    {
        int port = ReserveFreePort();

        PortProbe probe = PortHelper.Probe(port);

        Assert.True(probe.Ipv4Free);
        AssertIpv6Free(probe, expected: true);
        Assert.True(PortHelper.IsPortAvailable(port));
        Assert.Null(probe.HeldFamilies());
    }

    [Fact]
    public void Probe_ReportsIpv4Held_WhenAListenerOwnsIpv4()
    {
        using TcpListener listener = ListenOn(IPAddress.Loopback);
        int port = PortOf(listener);

        PortProbe probe = PortHelper.Probe(port);

        Assert.False(probe.Ipv4Free);
        Assert.False(PortHelper.IsPortAvailable(port));
        Assert.Equal("IPv4", probe.HeldFamilies());
    }

    [Fact]
    public void Probe_ReportsIpv6Held_WhileIpv4StaysBindable()
    {
        // The WORKSTAT02 shape: opcualds held [::]:4840, the IPv4-only probe saw nothing,
        // the bridge bound 0.0.0.0:4840 too, and delivery between the two listeners was
        // left indeterminate.
        using TcpListener listener = ListenOn(IPAddress.IPv6Loopback);
        int port = PortOf(listener);

        PortProbe probe = PortHelper.Probe(port);

        Assert.True(probe.Ipv4Free);
        AssertIpv6Free(probe, expected: false);
        Assert.Equal("IPv6", probe.HeldFamilies());
    }

    [Fact]
    public void IsPortAvailable_ReflectsOnlyTheIpv4Bind()
    {
        // Pins the warn-only decision: an IPv6-only holder must NOT make the port read as
        // unavailable, because moving the bridge there would put it on a port the
        // installer's build-time firewall rule does not cover.
        using TcpListener listener = ListenOn(IPAddress.IPv6Loopback);
        int port = PortOf(listener);

        Assert.True(PortHelper.IsPortAvailable(port));
    }

    [Fact]
    public void Probe_ReportsBothFamiliesHeld_WhenListenersOwnEach()
    {
        using TcpListener ipv4 = ListenOn(IPAddress.Loopback);
        int port = PortOf(ipv4);
        using TcpListener ipv6 = ListenOn(IPAddress.IPv6Loopback, port);

        PortProbe probe = PortHelper.Probe(port);

        Assert.False(probe.Ipv4Free);
        AssertIpv6Free(probe, expected: false);
        Assert.Equal("IPv4 and IPv6", probe.HeldFamilies());
    }

    [Fact]
    public void FindAvailablePort_SkipsAPortHeldOnIpv4()
    {
        using TcpListener listener = ListenOn(IPAddress.Loopback);
        int taken = PortOf(listener);

        int found = PortHelper.FindAvailablePort(taken, taken + 20);

        Assert.NotEqual(taken, found);
        Assert.True(found > taken);
    }

    private static void AssertIpv6Free(PortProbe probe, bool expected)
    {
        Assert.True(probe.Ipv6Free.HasValue, "expected IPv6 to be probeable on this host");
        Assert.Equal(expected, probe.Ipv6Free!.Value);
    }

    private static int PortOf(TcpListener listener)
    {
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A port nothing is listening on: bound to port 0, read back, released.</summary>
    private static int ReserveFreePort()
    {
        using TcpListener listener = ListenOn(IPAddress.Loopback);
        return PortOf(listener);
    }

    private static TcpListener ListenOn(IPAddress address, int port = 0)
    {
        TcpListener listener = new(address, port);
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Hold IPv6 alone, exactly like the LDS: a dual-mode socket would also claim
            // IPv4 and turn this into a different test.
            listener.Server.DualMode = false;
        }

        listener.Start();
        return listener;
    }
}
