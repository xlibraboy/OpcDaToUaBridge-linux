using OpcBridge.Hmi.Services;
using OpcBridge.Hmi.ViewModels;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Config page's Connect button. While connected it is normally disabled, but a bridge-row
/// edit (adding a bridge address, or changing one) must bring it back: otherwise the operator
/// has to disconnect first before the new address can be applied.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class HmiConnectButtonTests
{
    [Fact]
    public async Task EditingTheBridgeList_ReenablesConnect_AndConnectingAppliesIt()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(_ => { });
        string configPath = Path.Combine(
            Path.GetTempPath(),
            "hmi-connect-" + Guid.NewGuid().ToString("N"),
            "hmi-config.json");
        var vm = new MainViewModel(
            new BridgeConnectionManager(),
            new PopupWindowService(),
            ownsServices: true,
            configPath: configPath);

        try
        {
            string address = handle.Client.BaseAddress!.ToString().TrimEnd('/');
            vm.BridgeRows[0].Address = address;

            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.True(vm.IsConnected);
            Assert.False(vm.ConnectCommand.CanExecute(null));

            vm.AddBridgeCommand.Execute(null);
            BridgeRow added = vm.BridgeRows[^1];
            Assert.True(vm.ConnectCommand.CanExecute(null));

            added.Address = address;
            Assert.True(vm.ConnectCommand.CanExecute(null));

            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.True(vm.IsConnected);
            Assert.False(vm.ConnectCommand.CanExecute(null));

            vm.BridgeRows[0].Address = address + "/";
            Assert.True(vm.ConnectCommand.CanExecute(null));
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
