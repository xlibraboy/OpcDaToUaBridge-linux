using OpcBridge.Core;
using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Pins the application identity defaults introduced by the
/// OpcDaToUaBridge → OPC Bridge rename. If one of these fails, a default
/// still carries the old application name (or was changed unintentionally).
/// </summary>
public sealed class NamingDefaultsTests
{
    [Fact]
    public void MqttBrokerOptions_DefaultClientId_IsOpcBridge()
    {
        Assert.Equal("OpcBridge", new MqttBrokerOptions().ClientId);
    }

    [Fact]
    public void UaServerOptions_DefaultApplicationName_IsOpcBridge()
    {
        Assert.Equal("OpcBridge", new UaServerOptions().ApplicationName);
    }

    [Fact]
    public void OpcUaSourceClientOptions_DefaultApplicationName_IsOpcBridgeUaClient()
    {
        Assert.Equal("OpcBridge.UaClient", new OpcUaSourceClientOptions().ApplicationName);
    }
}
