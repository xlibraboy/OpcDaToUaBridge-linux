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
}
