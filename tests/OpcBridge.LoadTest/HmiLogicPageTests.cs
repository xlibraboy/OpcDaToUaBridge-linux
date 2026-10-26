using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;
using OpcBridge.Hmi.ViewModels;
using OpcBridge.Hmi.ViewModels.Logic;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Logic page after the Avalonia editor retired: it is the selected bridge's own /logic
/// page, so the embedded (or browser-opened) page is same-origin with the data it edits.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class HmiLogicPageTests
{
    [Fact]
    public void BridgeOption_BuildsTheBridgesOwnLogicPageAddress()
    {
        var connected = new LogicBridgeOption("b1", BridgeLinkState.Connected, "http://plant:8080/");

        Assert.Equal("http://plant:8080/logic", connected.PageUrl);
        Assert.Equal(string.Empty, new LogicBridgeOption("b1", BridgeLinkState.Failed).PageUrl);
    }

    [Fact]
    public async Task ThePageFollowsTheSelectedBridgesOwnLogicAddress()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(_ => { });
        string configPath = Path.Combine(
            Path.GetTempPath(),
            "hmi-logic-page-" + Guid.NewGuid().ToString("N"),
            "hmi-config.json");
        MainViewModel vm = new(
            new BridgeConnectionManager(),
            new PopupWindowService(),
            ownsServices: true,
            configPath: configPath);
        try
        {
            vm.BridgeRows[0].Address = handle.Client.BaseAddress!.ToString().TrimEnd('/');
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.True(vm.IsConnected);

            string expected = handle.Client.BaseAddress!.ToString().TrimEnd('/') + "/logic";
            Assert.Equal(expected, vm.Logic.WebPageUrl);
            Assert.True(vm.Logic.HasWebPageUrl);
            Assert.True(vm.Logic.IsBridgeOk);
        }
        finally
        {
            await vm.DisposeAsync();
            try
            {
                Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true);
            }
            catch
            {
            }
        }
    }
}
