using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;
using OpcBridge.Hmi.ViewModels;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Config page's Save button: the bridge list can be persisted without connecting, so a
/// prepared or edited list survives an exit. Save compares the rows with the saved config,
/// so an edit that is reverted greys it out again. Connect still saves as it always did.
/// </summary>
public sealed class HmiConfigSaveTests
{
    [Fact]
    public void DefaultConfigPath_IsAbsolute_SoSavingNeverLandsBesideTheExecutable()
    {
        string path = MainViewModel.DefaultConfigPath();

        Assert.True(Path.IsPathRooted(path), $"config path must be absolute, got '{path}'");
        Assert.Equal("hmi-config.json", Path.GetFileName(path));
        Assert.Equal("OpcBridge.Hmi", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task Save_WritesTheEditedRows_AndFollowsThemBackAndForth()
    {
        string configPath = Path.Combine(
            Path.GetTempPath(),
            "hmi-save-" + Guid.NewGuid().ToString("N"),
            "hmi-config.json");
        var vm = new MainViewModel(
            new BridgeConnectionManager(),
            new PopupWindowService(),
            ownsServices: true,
            configPath: configPath);

        try
        {
            // The rows as loaded match what is on disk, so there is nothing to save yet.
            Assert.False(vm.SaveConfigCommand.CanExecute(null));

            vm.BridgeRows[0].Address = "http://127.0.0.1:9099";
            vm.BridgeRows[0].Name = "line-a";
            Assert.True(vm.SaveConfigCommand.CanExecute(null));

            vm.SaveConfigCommand.Execute(null);
            Assert.False(vm.SaveConfigCommand.CanExecute(null));
            Assert.Contains("saved", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

            HmiClientConfig saved = HmiClientConfig.LoadOrDefault(configPath);
            HmiBridgeEndpoint bridge = Assert.Single(saved.Bridges);
            Assert.Equal("line-a", bridge.Id);
            Assert.Equal("http://127.0.0.1:9099", bridge.BaseUrl);

            // An address edit that is reverted greys Save out again.
            vm.BridgeRows[0].Address = "http://127.0.0.1:9098";
            Assert.True(vm.SaveConfigCommand.CanExecute(null));
            vm.BridgeRows[0].Address = "http://127.0.0.1:9099";
            Assert.False(vm.SaveConfigCommand.CanExecute(null));

            // Same for the name.
            vm.BridgeRows[0].Name = "line-b";
            Assert.True(vm.SaveConfigCommand.CanExecute(null));
            vm.BridgeRows[0].Name = "line-a";
            Assert.False(vm.SaveConfigCommand.CanExecute(null));

            // Values that normalize to the saved ones (a trailing slash, host case) are no change.
            vm.BridgeRows[0].Address = "HTTP://127.0.0.1:9099/";
            Assert.False(vm.SaveConfigCommand.CanExecute(null));

            // An edit on a second row counts too, and removing the row is its revert.
            vm.AddBridgeCommand.Execute(null);
            vm.BridgeRows[^1].Address = "http://127.0.0.1:9097";
            Assert.True(vm.SaveConfigCommand.CanExecute(null));
            vm.RemoveBridgeCommand.Execute(vm.BridgeRows[^1]);
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
}
