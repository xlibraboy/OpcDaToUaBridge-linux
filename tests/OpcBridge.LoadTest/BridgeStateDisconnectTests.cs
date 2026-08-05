using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class BridgeStateDisconnectTests
{
    private static BridgeState CreateState() => new(Options.Create(new BridgeOptions()));

    [Fact]
    public void GetBadQualityKeys_ReturnsOnlyBadQualityValues()
    {
        BridgeState state = CreateState();
        state.SetValue(new BridgeValue("ua-a", "goodTag", 1.0, DateTime.UtcNow, 192, true));
        state.SetValue(new BridgeValue("ua-a", "badTag", null, DateTime.UtcNow, 0, false));
        state.SetValue(new BridgeValue("ua-b", "otherGood", 2.0, DateTime.UtcNow, 192, true));

        IReadOnlyList<string> bad = state.GetBadQualityKeys();

        Assert.Single(bad);
        Assert.Contains("ua-a::badTag", bad, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetBadQualityKeys_EmptyWhenAllValuesGood()
    {
        BridgeState state = CreateState();
        state.SetValue(new BridgeValue("ua-a", "goodTag", 1.0, DateTime.UtcNow, 192, true));

        Assert.Empty(state.GetBadQualityKeys());
    }
}
