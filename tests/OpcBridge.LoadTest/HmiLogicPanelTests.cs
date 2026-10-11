using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpcBridge.Client;
using OpcBridge.Core;
using OpcBridge.Hmi.Services;
using OpcBridge.Hmi.ViewModels;
using OpcBridge.Hmi.ViewModels.Logic;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Logic page end-to-end against a real bridge host: definitions load and group, the editor
/// saves, rejects duplicates and deletes through the bridge's own API, the evaluated snapshot
/// lands on the result rows, and the bridge's logic pushes reach the manager.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class HmiLogicPanelTests
{
    private static async Task<TestAppHandle> StartAsync(params TagMapping[] mappings) =>
        await TestAppHandle.StartAsync(appDirectory =>
        {
            File.WriteAllText(
                Path.Combine(appDirectory, "mappings.json"),
                JsonSerializer.Serialize(mappings));
        });

    private static TagMapping PermitTag() => new()
    {
        SourceId = "sim",
        ItemId = "ns=2;s=Status/Line01.Permit",
        DisplayName = "Line 01 Start Permit",
        DataType = "Boolean",
        Enabled = true
    };

    private static LogicBlockDto Block(string name, string group) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Group = group,
        Kind = LogicBlockKinds.Interlock,
        Conditions =
        {
            new LogicConditionDto
            {
                Id = Guid.NewGuid(),
                Text = name + " permit must be given",
                SourceId = "sim",
                ItemId = "ns=2;s=Status/Line01.Permit",
                Op = LogicConditionOps.On
            }
        }
    };

    private static async Task SaveBlockAsync(TestAppHandle handle, LogicBlockDto block)
    {
        using HttpResponseMessage response = await handle.Client.PostAsJsonAsync("/api/logic/blocks", new { block });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<int> BlockCountAsync(TestAppHandle handle)
    {
        using JsonDocument document = await handle.GetJsonAsync("/api/logic");
        return document.RootElement.GetProperty("blocks").GetArrayLength();
    }

    private static MainViewModel NewViewModel(out string configPath)
    {
        configPath = Path.Combine(
            Path.GetTempPath(),
            "hmi-logic-" + Guid.NewGuid().ToString("N"),
            "hmi-config.json");
        return new MainViewModel(
            new BridgeConnectionManager(),
            new PopupWindowService(),
            ownsServices: true,
            configPath: configPath);
    }

    private static async Task ConnectAsync(MainViewModel vm, TestAppHandle handle)
    {
        vm.BridgeRows[0].Address = handle.Client.BaseAddress!.ToString().TrimEnd('/');
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        await vm.Logic.LoadSelectedBridgeAsync();
    }

    private static void DeleteConfig(string configPath)
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task EditorLoadsBlocksGroupedUnderTheirInterlockGroup()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        await SaveBlockAsync(handle, Block("Line 01 Start", "Primary Arm"));
        await SaveBlockAsync(handle, Block("Line 01 Stop", ""));

        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);

            Assert.True(vm.Logic.IsEditingEnabled);
            Assert.True(vm.Logic.HasBlocks);
            Assert.Equal(new[] { "Ungrouped", "Primary Arm" }, vm.Logic.BlockGroups.Select(group => group.Name));
            Assert.All(vm.Logic.BlockGroups, group => Assert.True(group.ShowHeading));

            LogicBlockRowViewModel grouped = Assert.Single(vm.Logic.BlockGroups[1].Blocks);
            Assert.Equal("Line 01 Start", grouped.Name);
            Assert.Equal("Interlock", grouped.KindLabel);
            Assert.Equal("NO DATA", grouped.StateWord);
            Assert.Contains("no data", grouped.ReasonLine, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("0 ready · 0 blocked · 2 no data", vm.Logic.SummaryText);
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task SavingANewBlockCreatesItOnTheBridge()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);

            vm.Logic.NewBlockCommand.Execute(null);
            vm.Logic.BlockName = "Line 03 Start";
            vm.Logic.BlockGroup = "Primary Arm";
            LogicConditionRowViewModel row = Assert.Single(vm.Logic.Conditions);
            row.Text = "Line 03 permit must be given";
            row.SelectedSource = row.Sources.First(source => source.SourceId == "sim");
            row.SelectedTag = row.Items.First();

            Assert.True(vm.Logic.SaveCommand.CanExecute(null));
            await vm.Logic.SaveCommand.ExecuteAsync(null);

            Assert.Equal("✓ Saved.", vm.Logic.EditorMessage);
            Assert.False(vm.Logic.IsDirty);
            Assert.Equal(1, await BlockCountAsync(handle));
            Assert.Equal("Line 03 Start", vm.Logic.SelectedBlock?.Name);
            Assert.Contains(
                "Line 03 Start",
                vm.Logic.BlockGroups.SelectMany(group => group.Blocks).Select(block => block.Name));
            Assert.False(vm.Logic.SaveCommand.CanExecute(null));
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task DuplicateNameSurfacesTheBridgesOwnError()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        await SaveBlockAsync(handle, Block("Line 01 Start", ""));

        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);

            vm.Logic.NewBlockCommand.Execute(null);
            vm.Logic.BlockName = "Line 01 Start";
            LogicConditionRowViewModel row = Assert.Single(vm.Logic.Conditions);
            row.Text = "Duplicate";
            row.SelectedSource = row.Sources.First(source => source.SourceId == "sim");
            row.SelectedTag = row.Items.First();

            await vm.Logic.SaveCommand.ExecuteAsync(null);

            Assert.Contains("already exists", vm.Logic.EditorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.True(vm.Logic.IsDirty);
            Assert.Equal(1, await BlockCountAsync(handle));
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task DeleteRemovesTheBlockFromTheBridge()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        await SaveBlockAsync(handle, Block("Line 01 Start", ""));

        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);

            LogicBlockRowViewModel row = Assert.Single(vm.Logic.BlockGroups.SelectMany(group => group.Blocks));
            await vm.Logic.DeleteBlockCommand.ExecuteAsync(row);

            Assert.Equal("✓ Block deleted.", vm.Logic.EditorMessage);
            Assert.False(vm.Logic.HasBlocks);
            Assert.Equal(0, await BlockCountAsync(handle));
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task EvaluatedStateLandsOnTheResultRows()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        LogicBlockDto block = Block("Line 01 Start", "Primary Arm");
        await SaveBlockAsync(handle, block);
        Guid conditionId = block.Conditions[0].Id;

        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);

            vm.Logic.ApplyState(
                "default",
                new LogicStateSnapshot
                {
                    Version = 42,
                    Blocks =
                    {
                        new LogicBlockStateDto
                        {
                            Id = block.Id,
                            State = LogicBlockStates.Blocked,
                            Reason = "Line 01 Start permit must be given",
                            Elements =
                            {
                                new LogicElementStateDto
                                {
                                    Id = conditionId,
                                    Kind = LogicElementKinds.Contact,
                                    Depth = 0,
                                    State = LogicConditionStates.False,
                                    Should = "1",
                                    Matches = false,
                                    ValueText = "Blocked"
                                }
                            }
                        }
                    }
                });

            // Asserted synchronously: the version change only schedules a definitions refetch.
            LogicResultElementRowViewModel row = Assert.Single(vm.Logic.ResultElements);
            Assert.Equal("NO", row.KindLabel);
            Assert.Equal("✗", row.Mark);
            Assert.Equal("0", row.Actual);
            Assert.Equal("should 1 · actual 0", row.ShouldLine);
            Assert.Equal("Blocked", row.ValueText);
            Assert.Contains("Line 01 Start Permit", row.TagLabel);
            Assert.Equal("Blocked by: Line 01 Start permit must be given", vm.Logic.ResultReason);
            Assert.Equal("BLOCKED", vm.Logic.ResultStateWord);
            Assert.Equal("BLOCKED", vm.Logic.BlockGroups[0].Blocks[0].StateWord);
            Assert.Equal("0 ready · 1 blocked", vm.Logic.SummaryText);
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task EditingIsDisabledWithoutABridge()
    {
        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            Assert.Null(vm.Logic.SelectedBridge);
            Assert.False(vm.Logic.IsEditingEnabled);
            Assert.False(vm.Logic.SaveCommand.CanExecute(null));
            Assert.False(vm.Logic.NewBlockCommand.CanExecute(null));
            Assert.False(string.IsNullOrWhiteSpace(vm.Logic.EditingHint));
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task AnUnreachableBridge_LeavesEditingDisabled()
    {
        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            vm.BridgeRows[0].Address = "http://127.0.0.1:9";
            await vm.ConnectCommand.ExecuteAsync(null);

            Assert.False(vm.IsConnected);
            Assert.Null(vm.Logic.SelectedBridge);
            Assert.False(vm.Logic.IsEditingEnabled);
            Assert.False(vm.Logic.SaveCommand.CanExecute(null));
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task LogicPushesReachTheManagerWithTheBridgeId()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        var connections = new BridgeConnectionManager();
        string configPath = Path.Combine(
            Path.GetTempPath(),
            "hmi-logic-push-" + Guid.NewGuid().ToString("N"),
            "hmi-config.json");
        var vm = new MainViewModel(connections, new PopupWindowService(), ownsServices: true, configPath: configPath);
        try
        {
            await ConnectAsync(vm, handle);

            var pushed = new TaskCompletionSource<(string BridgeId, LogicStateSnapshot Snapshot)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            connections.LogicChanged += (bridgeId, snapshot) => pushed.TrySetResult((bridgeId, snapshot));

            LogicBlockDto added = Block("Line 01 Start", "");
            await SaveBlockAsync(handle, added);

            Task completed = await Task.WhenAny(pushed.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.True(ReferenceEquals(completed, pushed.Task), "no logic push arrived over the hub");

            (string bridgeId, LogicStateSnapshot snapshot) = await pushed.Task;
            Assert.Equal("default", bridgeId);
            Assert.Contains(snapshot.Blocks, state => state.Id == added.Id);
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }
}
