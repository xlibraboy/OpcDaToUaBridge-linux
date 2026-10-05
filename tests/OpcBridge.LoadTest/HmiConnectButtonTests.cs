using OpcBridge.Hmi.Services;
using OpcBridge.Hmi.ViewModels;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Config page's Connect button. While connected it is normally disabled, but a bridge-row
/// edit (adding a bridge address, or changing one) must bring it back: otherwise the operator
/// has to disconnect first before the new address can be applied. Renames never light Connect
/// up — Save applies those to the live bridge itself.
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
            // Connecting saves the rows it connected with, so there is nothing left to save.
            Assert.False(vm.SaveConfigCommand.CanExecute(null));

            vm.AddBridgeCommand.Execute(null);
            BridgeRow added = vm.BridgeRows[^1];
            // An empty row builds no bridge, so the config is still the connected one.
            Assert.False(vm.ConnectCommand.CanExecute(null));
            Assert.False(vm.SaveConfigCommand.CanExecute(null));

            added.Address = address;
            Assert.True(vm.ConnectCommand.CanExecute(null));
            Assert.True(vm.SaveConfigCommand.CanExecute(null));

            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.True(vm.IsConnected);
            Assert.False(vm.ConnectCommand.CanExecute(null));
            Assert.False(vm.SaveConfigCommand.CanExecute(null));

            // Renaming a bridge never involves Connect: the button stays greyed out. Save is
            // what applies the new name to the live bridge itself.
            string originalName = vm.BridgeRows[0].Name;
            vm.BridgeRows[0].Name = "renamed";
            Assert.False(vm.ConnectCommand.CanExecute(null));
            Assert.True(vm.SaveConfigCommand.CanExecute(null));

            await vm.SaveConfigCommand.ExecuteAsync(null);
            Assert.Contains("renamed", vm.BridgeSummary, StringComparison.OrdinalIgnoreCase);
            Assert.False(vm.SaveConfigCommand.CanExecute(null));
            Assert.False(vm.ConnectCommand.CanExecute(null));

            // Reverting the name is another save-only edit, applied by the next Save.
            vm.BridgeRows[0].Name = originalName;
            Assert.True(vm.SaveConfigCommand.CanExecute(null));
            await vm.SaveConfigCommand.ExecuteAsync(null);
            Assert.Contains("default", vm.BridgeSummary, StringComparison.OrdinalIgnoreCase);
            Assert.False(vm.SaveConfigCommand.CanExecute(null));

            vm.BridgeRows[0].Address = "http://127.0.0.1:9";
            Assert.True(vm.ConnectCommand.CanExecute(null));
            Assert.True(vm.SaveConfigCommand.CanExecute(null));
            vm.BridgeRows[0].Address = address;
            Assert.False(vm.ConnectCommand.CanExecute(null));
            Assert.False(vm.SaveConfigCommand.CanExecute(null));

            // Removing the extra bridge is the same revert through the row list.
            vm.RemoveBridgeCommand.Execute(added);
            Assert.True(vm.ConnectCommand.CanExecute(null));
            Assert.True(vm.SaveConfigCommand.CanExecute(null));
            vm.AddBridgeCommand.Execute(null);
            vm.BridgeRows[^1].Address = address;
            Assert.False(vm.ConnectCommand.CanExecute(null));
            Assert.False(vm.SaveConfigCommand.CanExecute(null));
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

    [Fact]
    public async Task SavingARenameWithAnAddressChange_WaitsForConnect()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(_ => { });
        string configPath = Path.Combine(
            Path.GetTempPath(),
            "hmi-rename-" + Guid.NewGuid().ToString("N"),
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
            Assert.Equal("default", vm.BridgeSummary);

            // Rename the live bridge and add another one: the list no longer matches the live
            // connections, so Save persists the rename but leaves it for Connect to apply.
            vm.BridgeRows[0].Name = "renamed";
            vm.AddBridgeCommand.Execute(null);
            vm.BridgeRows[^1].Address = address;
            await vm.SaveConfigCommand.ExecuteAsync(null);

            Assert.Equal("default", vm.BridgeSummary);
            Assert.Contains("Connect", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(vm.SaveConfigCommand.CanExecute(null));

            // Connect applies the whole list, rename included.
            Assert.True(vm.ConnectCommand.CanExecute(null));
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.Contains("renamed", vm.BridgeSummary, StringComparison.OrdinalIgnoreCase);
            Assert.False(vm.ConnectCommand.CanExecute(null));
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

    [Fact]
    public async Task SavingSwappedBridgeNames_RelabelsBothLiveConnections()
    {
        await using TestAppHandle first = await TestAppHandle.StartAsync(_ => { });
        await using TestAppHandle second = await TestAppHandle.StartAsync(_ => { });
        string configPath = Path.Combine(
            Path.GetTempPath(),
            "hmi-swap-" + Guid.NewGuid().ToString("N"),
            "hmi-config.json");
        var connections = new BridgeConnectionManager();
        var vm = new MainViewModel(
            connections,
            new PopupWindowService(),
            ownsServices: true,
            configPath: configPath);

        try
        {
            string addressA = first.Client.BaseAddress!.ToString().TrimEnd('/');
            string addressB = second.Client.BaseAddress!.ToString().TrimEnd('/');
            vm.BridgeRows[0].Name = "a";
            vm.BridgeRows[0].Address = addressA;
            vm.AddBridgeCommand.Execute(null);
            vm.BridgeRows[^1].Name = "b";
            vm.BridgeRows[^1].Address = addressB;
            await vm.ConnectCommand.ExecuteAsync(null);

            // Swap the two names in one save: each relabel connects under the id the other
            // relabel is vacating, so a per-bridge disconnect/reconnect would tear the first
            // one down again. Both sessions must survive, now pointing at crossed addresses.
            vm.BridgeRows[0].Name = "b";
            vm.BridgeRows[^1].Name = "a";
            await vm.SaveConfigCommand.ExecuteAsync(null);

            Assert.True(connections.TryGetSession("a", out BridgeConnectionManager.BridgeSession? sessionA));
            Assert.True(connections.TryGetSession("b", out BridgeConnectionManager.BridgeSession? sessionB));
            Assert.Equal(addressB, sessionA!.BaseUrl);
            Assert.Equal(addressA, sessionB!.BaseUrl);
            Assert.False(vm.SaveConfigCommand.CanExecute(null));
            Assert.False(vm.ConnectCommand.CanExecute(null));
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
