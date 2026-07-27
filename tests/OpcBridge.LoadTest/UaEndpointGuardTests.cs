using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class UaEndpointGuardTests
{
    [Theory]
    [InlineData("opc.tcp://127.0.0.1:4840/OpcBridge", "opc.tcp://0.0.0.0:4840/OpcBridge", true)]
    [InlineData("opc.tcp://localhost:4840/OpcBridge", "opc.tcp://0.0.0.0:4840/OpcBridge", true)]
    [InlineData("opc.tcp://kepware:49320", "opc.tcp://0.0.0.0:4840/OpcBridge", false)]
    [InlineData("opc.tcp://127.0.0.1:4841/OpcBridge", "opc.tcp://0.0.0.0:4840/OpcBridge", false)]
    public void TargetsSelf_DetectsLoopbackSamePortPath(string candidate, string server, bool expected)
    {
        Assert.Equal(expected, UaEndpointGuard.TargetsSelf(candidate, server));
    }

    [Theory]
    [InlineData("opc.tcp://0.0.0.0:4840/OpcBridge", "opc.tcp://0.0.0.0:4840/OpcBridge")] // verbatim bind URL pasted
    [InlineData("opc.tcp://0.0.0.0:4840/OpcBridge", "opc.tcp://127.0.0.1:4840/OpcBridge")]
    [InlineData("opc.tcp://[::]:4840/OpcBridge", "opc.tcp://0.0.0.0:4840/OpcBridge")]
    public void TargetsSelf_WildcardCandidateAgainstWildcardServer_IsSelf(string candidate, string server)
    {
        Assert.True(UaEndpointGuard.TargetsSelf(candidate, server));
    }

    [Fact]
    public void TargetsSelf_OwnLanIpAgainstWildcardServer_IsSelf()
    {
        string? ownIp = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
            .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(ip => ip.ToString())
            .FirstOrDefault(ip => !ip.StartsWith("127."));

        if (ownIp is null)
        {
            // No non-loopback IPv4 on this host; nothing to assert.
            return;
        }

        string candidate = $"opc.tcp://{ownIp}:4840/OpcBridge";
        Assert.True(UaEndpointGuard.TargetsSelf(candidate, "opc.tcp://0.0.0.0:4840/OpcBridge"));
    }

    [Fact]
    public void TargetsSelf_UnrelatedIpAgainstWildcardServer_IsNotSelf()
    {
        Assert.False(UaEndpointGuard.TargetsSelf(
            "opc.tcp://203.0.113.10:4840/OpcBridge",
            "opc.tcp://0.0.0.0:4840/OpcBridge"));
    }
}
