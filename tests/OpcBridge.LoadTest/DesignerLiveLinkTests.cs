using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Designer.Services;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Designer's live link against a real test host: the mapped-tag snapshot must land in the
/// shared cache (that is what makes bound widgets preview real values).
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class DesignerLiveLinkTests
{
    [Fact]
    public async Task StartAsync_LoadsMappedTagsIntoTheCache_AndStopGoesOffline()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(
            dir => DesignerBridgeClientTests.WriteAppsettings(dir));
        var cache = new MultiBridgeTagCache();
        var link = new DesignerLiveLink(cache);
        int changes = 0;

        await link.StartAsync(
            handle.Client.BaseAddress!.ToString().TrimEnd('/'),
            "default",
            () => Interlocked.Increment(ref changes),
            CancellationToken.None);

        Assert.NotEqual(DesignerLiveState.Offline, link.State);
        Assert.True(changes >= 1);
        Assert.True(cache.TryGet(TagBindingKey.Create("default", "line1", "Random.Int1"), out MultiBridgeTagEntry? entry));
        Assert.NotNull(entry);
        Assert.Equal("Int1", entry!.DisplayName);

        await link.DisposeAsync();
        Assert.Equal(DesignerLiveState.Offline, link.State);
    }

    [Fact]
    public async Task StartAsync_UnreachableBridge_ReportsOffline()
    {
        var cache = new MultiBridgeTagCache();
        var link = new DesignerLiveLink(cache);

        await link.StartAsync("http://127.0.0.1:1", "default", () => { }, CancellationToken.None);

        Assert.Equal(DesignerLiveState.Offline, link.State);
        Assert.NotNull(link.LastError);
        await link.DisposeAsync();
    }
}
