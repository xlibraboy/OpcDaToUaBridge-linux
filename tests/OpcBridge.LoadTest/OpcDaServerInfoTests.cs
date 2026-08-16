using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

public class OpcDaServerInfoTests
{
    [Fact]
    public void Describe_FullInfo_IncludesSpecVersionAndVendor()
    {
        OpcDaServerInfo info = new("3.0", 2, 0, 1, "MatrikonOPC Simulation Server", "Running");

        Assert.Equal("OPC DA 3.0 · v2.0.1 · MatrikonOPC Simulation Server", info.Describe());
    }

    [Fact]
    public void Describe_ZeroedVersion_OmitsVersionButKeepsSpec()
    {
        // GetStatus failed or reported no version — spec level from the interface
        // probe must still surface.
        OpcDaServerInfo info = new("2.0", 0, 0, 0, null, "Unknown");

        Assert.Equal("OPC DA 2.0", info.Describe());
    }

    [Fact]
    public void Describe_MinorOnlyVersion_FormatsTwoParts()
    {
        OpcDaServerInfo info = new("2.0", 1, 5, 0, null, "Running");

        Assert.Equal("OPC DA 2.0 · v1.5", info.Describe());
    }

    [Fact]
    public void Describe_NonRunningState_IsAppended()
    {
        OpcDaServerInfo info = new("1.0", 1, 2, 3, "Vendor", "Failed");

        Assert.Equal("OPC DA 1.0 · v1.2.3 · Failed · Vendor", info.Describe());
    }

    [Fact]
    public void Describe_TrimsVendorWhitespace()
    {
        OpcDaServerInfo info = new("3.0", 2, 0, 0, "  Acme OPC   ", "Running");

        Assert.Equal("OPC DA 3.0 · v2.0 · Acme OPC", info.Describe());
    }

    [Fact]
    public void DescribeState_MapsKnownStates()
    {
        Assert.Equal("Running", OpcDaServerInfo.DescribeState(1));
        Assert.Equal("Failed", OpcDaServerInfo.DescribeState(2));
        Assert.Equal("NoConfig", OpcDaServerInfo.DescribeState(3));
        Assert.Equal("Suspended", OpcDaServerInfo.DescribeState(4));
        Assert.Equal("Test", OpcDaServerInfo.DescribeState(5));
        Assert.Equal("CommFault", OpcDaServerInfo.DescribeState(6));
        Assert.Equal("Unknown", OpcDaServerInfo.DescribeState(0));
        Assert.Equal("Unknown", OpcDaServerInfo.DescribeState(99));
    }
}
