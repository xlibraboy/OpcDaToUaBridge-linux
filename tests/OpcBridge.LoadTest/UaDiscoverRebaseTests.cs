using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class UaDiscoverRebaseTests
{
    private static Uri Probed(string url) => new(url);

    [Theory]
    [InlineData("opc.tcp://0.0.0.0:4840/opcuasim/", "opc.tcp://172.17.0.1:49322/opcuasim/")]
    [InlineData("opc.tcp://0.0.0.0:4840", "opc.tcp://172.17.0.1:49322/")]
    [InlineData("opc.tcp://localhost:4840/opcuasim/", "opc.tcp://172.17.0.1:49322/opcuasim/")]
    [InlineData("opc.tcp://127.0.0.1:4840/opcuasim/", "opc.tcp://172.17.0.1:49322/opcuasim/")]
    public void RebaseDiscoveryUrl_RebasesNonRoutableAdvertisedHost(string advertised, string expected)
    {
        string result = OpcUaBrowseService.RebaseDiscoveryUrl(advertised, Probed("opc.tcp://172.17.0.1:49322/opcuasim/"));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("opc.tcp://192.168.1.50:4840/opcuasim/")]
    [InlineData("opc.tcp://172.17.0.1:4840/opcuasim/")]
    [InlineData("opc.tcp://host.docker.internal:4840/opcuasim/")]
    public void RebaseDiscoveryUrl_KeepsRoutableAdvertisedUrl(string advertised)
    {
        string result = OpcUaBrowseService.RebaseDiscoveryUrl(advertised, Probed("opc.tcp://172.17.0.1:49322/opcuasim/"));
        Assert.Equal(advertised, result);
    }

    [Fact]
    public void RebaseDiscoveryUrl_PreservesPathAndScheme()
    {
        string result = OpcUaBrowseService.RebaseDiscoveryUrl(
            "opc.tcp://0.0.0.0:4840/custom/path?x=1",
            Probed("opc.tcp://10.0.0.5:5555/somewhere"));
        Assert.Equal("opc.tcp://10.0.0.5:5555/custom/path?x=1", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    public void RebaseDiscoveryUrl_ReturnsInputWhenUnparsable(string? advertised)
    {
        string result = OpcUaBrowseService.RebaseDiscoveryUrl(advertised, Probed("opc.tcp://172.17.0.1:49322/opcuasim/"));
        Assert.Equal(advertised ?? string.Empty, result);
    }
}
