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
            // The harness copies the test output directory, which can hold files left by the
            // in-process store tests; start from a clean logic slate.
            foreach (string stray in new[] { "logic.json", "logic-notes.json", "logic-tags.json" })
            {
                string strayPath = Path.Combine(appDirectory, stray);
                if (File.Exists(strayPath))
                {
                    File.Delete(strayPath);
                }
            }

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

    private static LogicBlockRowViewModel Row(MainViewModel vm, string name) =>
        vm.Logic.BlockAreas.SelectMany(area => area.Groups).SelectMany(group => group.Blocks)
            .First(row => row.Name == name);

    /// <summary>
    /// Waits out the bridge's broadcast flush window (four of the 100 ms periods), so a push an
    /// earlier call already triggered — a logic save marks the logic dirty — has gone out before
    /// the test connects. The bridge's own snapshot (unknown in this host, which has no source
    /// values) is applied on the dispatcher thread and would otherwise race a synthetic snapshot
    /// the test applies and asserts in the same breath.
    /// </summary>
    private static Task SettleBroadcastAsync() => Task.Delay(400);

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

            // No block carries an area yet: the single wrapper heading stays hidden and the list
            // reads exactly as it did before areas existed.
            LogicAreaViewModel area = Assert.Single(vm.Logic.BlockAreas);
            Assert.False(area.ShowHeading);
            Assert.Equal(new[] { "Ungrouped", "Primary Arm" }, area.Groups.Select(group => group.Name));
            Assert.All(area.Groups, group => Assert.True(group.ShowHeading));

            LogicBlockRowViewModel grouped = Assert.Single(area.Groups[1].Blocks);
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
    public async Task AreasHeadTheBlockListAndGroupsSitInsideThem()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        LogicBlockDto block = Block("Primary Arm Up", "Primary Arm");
        block.Area = "DRY END";
        await SaveBlockAsync(handle, block);

        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);

            LogicAreaViewModel area = Assert.Single(vm.Logic.BlockAreas);
            Assert.Equal("DRY END", area.Name);
            Assert.True(area.ShowHeading);
            LogicGroupViewModel group = Assert.Single(area.Groups);
            Assert.Equal("Primary Arm", group.Name);
            LogicBlockRowViewModel row = Assert.Single(group.Blocks);
            Assert.Equal("Primary Arm Up", row.Name);
            Assert.True(row.HasArea);
            Assert.Equal("DRY END", row.Area);
            Assert.Equal("DRY END", vm.Logic.AreaTags.Name);
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
            vm.Logic.BlockArea = "PRIMARY ARM";

            // The tag is assigned once, on the Tags tab: name, source and tag together.
            LogicTagEntryViewModel entry = vm.Logic.AreaTags.NewTag();
            entry.Name = "LINE_03_PERMIT";
            entry.SelectedSource = entry.Sources.First(source => source.SourceId == "sim");
            entry.SelectedTag = entry.Items.First();

            LogicConditionRowViewModel row = Assert.Single(vm.Logic.Conditions);
            row.Text = "Line 03 permit must be given";
            row.SelectedBlockTag = entry;
            Assert.Same(entry, Assert.Single(vm.Logic.AreaTags.Entries));

            Assert.True(vm.Logic.SaveCommand.CanExecute(null));
            await vm.Logic.SaveCommand.ExecuteAsync(null);

            Assert.Equal("✓ Saved.", vm.Logic.EditorMessage);
            Assert.False(vm.Logic.IsDirty);
            Assert.Equal(1, await BlockCountAsync(handle));
            Assert.Equal("Line 03 Start", vm.Logic.SelectedBlock?.Name);
            Assert.Contains(
                "Line 03 Start",
                vm.Logic.BlockAreas.SelectMany(area => area.Groups).SelectMany(group => group.Blocks).Select(block => block.Name));
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
            vm.Logic.BlockArea = "PRIMARY ARM";
            LogicTagEntryViewModel entry = vm.Logic.AreaTags.NewTag();
            entry.Name = "LINE_01_PERMIT";
            entry.SelectedSource = entry.Sources.First(source => source.SourceId == "sim");
            entry.SelectedTag = entry.Items.First();
            LogicConditionRowViewModel row = Assert.Single(vm.Logic.Conditions);
            row.Text = "Duplicate";
            row.SelectedBlockTag = entry;

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

            LogicBlockRowViewModel row = Assert.Single(
                vm.Logic.BlockAreas.SelectMany(area => area.Groups).SelectMany(group => group.Blocks));
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

        // The save marks the logic dirty, so the bridge pushes its own evaluation (unknown in
        // this host, which has no source values) on its next 100 ms flush. Let that go out
        // before the client connects: a push landing later is applied on the dispatcher thread
        // and would overwrite the synthetic snapshot asserted below — the flake this test had.
        await SettleBroadcastAsync();

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
            Assert.Equal("BLOCKED", vm.Logic.BlockAreas[0].Groups[0].Blocks[0].StateWord);
            Assert.Equal("0 ready · 1 blocked", vm.Logic.SummaryText);
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public void NestedResultRows_CarryARailPerEnclosingLevel()
    {
        var root = new LogicElementDto
        {
            Id = Guid.NewGuid(),
            Kind = LogicElementKinds.And,
            Inputs =
            {
                new LogicElementDto
                {
                    Id = Guid.NewGuid(),
                    Kind = LogicElementKinds.Contact,
                    Op = LogicConditionOps.On,
                    ItemId = "ns=2;s=Status/Line01.Permit"
                }
            }
        };

        var rows = new List<LogicResultElementRowViewModel>();
        foreach ((LogicElementDto element, int depth) in LogicNetwork.Walk(new[] { root }))
        {
            rows.Add(new LogicResultElementRowViewModel(element, depth, LogicTagCatalog.Empty));
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal("AND", rows[0].KindLabel);
        Assert.Empty(rows[0].Guides);
        Assert.Equal("NO", rows[1].KindLabel);
        Assert.Single(rows[1].Guides);
    }

    [Fact]
    public async Task TheDetailPane_NamesWhatToDoWhenItIsEmpty()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            Assert.Equal("NO BRIDGE", vm.Logic.ResultEmptyTitle);
            Assert.Equal("Connect a bridge to load and author its plant logic.", vm.Logic.ResultEmptyMessage);
            Assert.Equal("Connect a bridge to load its plant logic.", vm.Logic.EmptyBlocksMessage);

            await ConnectAsync(vm, handle);
            Assert.Equal("NO BLOCKS YET", vm.Logic.ResultEmptyTitle);
            Assert.Equal("Use New block to author the first one.", vm.Logic.ResultEmptyMessage);
            Assert.Equal(
                "No logic blocks yet — use New block to author the first one.",
                vm.Logic.EmptyBlocksMessage);
            Assert.False(vm.Logic.ResultTitleVisible);

            vm.Logic.NewBlockCommand.Execute(null);
            Assert.Equal("NOT SAVED YET", vm.Logic.ResultEmptyTitle);
            Assert.Equal("Save the block to see its evaluated network.", vm.Logic.ResultEmptyMessage);
            Assert.True(vm.Logic.ResultTitleVisible);
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task SwitchingBlocks_NeverShowsThePreviousBlocksState()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        LogicBlockDto first = Block("Line 01 Start", "");
        LogicBlockDto second = Block("Line 02 Start", "");
        await SaveBlockAsync(handle, first);
        await SaveBlockAsync(handle, second);

        // Let the save-driven push go out before the client connects (see SettleBroadcastAsync).
        await SettleBroadcastAsync();

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
                            Id = first.Id,
                            State = LogicBlockStates.Blocked,
                            Reason = "Line 01 Start permit must be given"
                        }
                    }
                });

            vm.Logic.SelectBlockCommand.Execute(Row(vm, "Line 01 Start"));
            Assert.Equal("BLOCKED", vm.Logic.ResultStateWord);

            // The second block has no state in the cache: the pane must say so at once instead of
            // showing the first block's word until the next snapshot lands.
            vm.Logic.SelectBlockCommand.Execute(Row(vm, "Line 02 Start"));
            Assert.Equal("READING…", vm.Logic.ResultStateWord);
            Assert.True(vm.Logic.IsResultWarn);
            Assert.False(vm.Logic.IsResultBad);
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

    [Fact]
    public async Task AreaTags_SaveWithTheBlockAndTheAreaSharesThem()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);
            vm.Logic.IsTagsTab = true;

            vm.Logic.NewBlockCommand.Execute(null);
            vm.Logic.BlockName = "Dry end start";
            vm.Logic.BlockArea = "DRYEND";

            // The area is the tag-sharing key: typing it points the tab at DRYEND's tags.
            Assert.Equal("DRYEND", vm.Logic.AreaTags.Name);
            LogicTagEntryViewModel entry = vm.Logic.AreaTags.NewTag();
            entry.Name = "START_PB";
            entry.SelectedSource = entry.Sources.First(source => source.SourceId == "sim");
            entry.SelectedTag = entry.Items.First();

            LogicConditionRowViewModel row = Assert.Single(vm.Logic.Conditions);
            row.Text = "Dry end start permit must be given";
            row.SelectedBlockTag = entry;
            Assert.False(row.HasStoredTag);

            Assert.True(vm.Logic.SaveCommand.CanExecute(null));
            await vm.Logic.SaveCommand.ExecuteAsync(null);

            Assert.Equal("✓ Saved.", vm.Logic.EditorMessage);
            Assert.False(vm.Logic.IsDirty);

            using JsonDocument document = await handle.GetJsonAsync("/api/logic");
            JsonElement source = document.RootElement.GetProperty("tagSources")[0];
            Assert.Equal("DRYEND", source.GetProperty("name").GetString());
            Assert.Equal("START_PB", source.GetProperty("tags")[0].GetProperty("name").GetString());
            Assert.Equal("ns=2;s=Status/Line01.Permit", source.GetProperty("tags")[0].GetProperty("itemId").GetString());

            JsonElement stored = document.RootElement.GetProperty("blocks")[0];
            Assert.Equal("DRYEND", stored.GetProperty("tagSource").GetString());
            Assert.Equal("START_PB", stored.GetProperty("conditions")[0].GetProperty("tag").GetString());
            Assert.Equal(string.Empty, stored.GetProperty("conditions")[0].GetProperty("itemId").GetString());

            // A second block of the same area already reads the assigned entry — the point of
            // assigning once — and the tab says how many blocks carry the area.
            vm.Logic.NewBlockCommand.Execute(null);
            vm.Logic.BlockName = "Dry end stop";
            vm.Logic.BlockArea = "DRYEND";

            LogicTagEntryViewModel shared = Assert.Single(vm.Logic.AreaTags.Entries);
            Assert.Equal("START_PB", shared.Name);
            Assert.True(shared.IsAssigned);
            Assert.Equal("DRYEND", vm.Logic.AreaTags.Name);
            Assert.Equal(1, vm.Logic.AreaTags.UsedByCount);
            Assert.True(vm.Logic.AreaTags.HasUsedBy);
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task AnEmptyTag_IsAssignedLaterAndTheBlockReadsItOnceBound()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);
            vm.Logic.IsTagsTab = true;

            vm.Logic.NewBlockCommand.Execute(null);
            vm.Logic.BlockName = "Dry end start";
            vm.Logic.BlockArea = "DRYEND";
            LogicTagEntryViewModel entry = vm.Logic.AreaTags.NewTag();
            entry.Name = "START_PB";
            LogicConditionRowViewModel row = Assert.Single(vm.Logic.Conditions);
            row.Text = "Dry end start permit must be given";
            row.SelectedBlockTag = entry;

            // Created empty and saved: the block is stored and reads unknown, naming the entry —
            // never a silent false.
            await vm.Logic.SaveCommand.ExecuteAsync(null);

            // The save's own push moves the store version, so the panel refetches the definitions
            // (and re-reads the Tags tab) a moment later. Let it land before editing the entry,
            // or the reload would replace the edit this test is about to make.
            await SettleBroadcastAsync();

            using (JsonDocument unassigned = await handle.GetJsonAsync("/api/logic/state"))
            {
                Assert.Equal(
                    "no data for START_PB (not assigned)",
                    unassigned.RootElement.GetProperty("blocks")[0].GetProperty("reason").GetString());
            }

            // Assign later, on the tab, and save the tags alone: the block now reads the real tag.
            // The save rebuilt the draft from the bridge, so the entry is re-read from the tab.
            LogicTagEntryViewModel stored = Assert.Single(vm.Logic.AreaTags.Entries);
            Assert.Equal("START_PB", stored.Name);
            stored.SelectedSource = stored.Sources.First(source => source.SourceId == "sim");
            stored.SelectedTag = stored.Items.First();
            Assert.True(vm.Logic.IsDirty);
            await vm.Logic.SaveCommand.ExecuteAsync(null);

            Assert.Equal("✓ Area tags saved.", vm.Logic.EditorMessage);
            Assert.False(vm.Logic.IsDirty);

            using JsonDocument assigned = await handle.GetJsonAsync("/api/logic/state");
            Assert.Equal(
                "no data for Line 01 Start Permit",
                assigned.RootElement.GetProperty("blocks")[0].GetProperty("reason").GetString());
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task ATagAssignedOnTheTabIsPickedOnARowAndSharedByTheArea()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);

            // The area is assigned first (the New-block dialog's job), then the tags are assigned
            // on the tab — name, source and tag together — and a row picks one of them.
            vm.Logic.NewBlockPrompt = (_, _) =>
                Task.FromResult<LogicNewBlockDraft?>(new LogicNewBlockDraft("Dry end start", "Primary Arm", "DRYEND"));
            Assert.True(await vm.Logic.StartNewBlockAsync());
            Assert.Equal("DRYEND", vm.Logic.AreaTags.Name);

            LogicTagEntryViewModel entry = vm.Logic.AreaTags.NewTag();
            entry.Name = "START_PB";
            entry.SelectedSource = entry.Sources.First(source => source.SourceId == "sim");
            entry.SelectedTag = entry.Items.First();
            Assert.Equal("ns=2;s=Status/Line01.Permit", entry.ItemId);

            LogicConditionRowViewModel row = Assert.Single(vm.Logic.Conditions);
            row.Text = "Dry end start permit must be given";
            row.SelectedBlockTag = entry;

            // The row carries the name and nothing else; the pair lives on the entry, which the
            // tab marks as read by this block.
            Assert.Equal("START_PB", row.Tag);
            Assert.False(row.HasStoredTag);
            Assert.True(entry.UsedByThisBlock);

            await vm.Logic.SaveCommand.ExecuteAsync(null);
            Assert.Equal("✓ Saved.", vm.Logic.EditorMessage);

            using JsonDocument document = await handle.GetJsonAsync("/api/logic");
            Assert.Equal("DRYEND", document.RootElement.GetProperty("tagSources")[0].GetProperty("name").GetString());
            JsonElement condition = document.RootElement.GetProperty("blocks")[0].GetProperty("conditions")[0];
            Assert.Equal("START_PB", condition.GetProperty("tag").GetString());
            Assert.Equal(string.Empty, condition.GetProperty("itemId").GetString());

            // A second block of the same area finds the entry and can pick it — sharing, same
            // area only.
            vm.Logic.NewBlockPrompt = (_, _) =>
                Task.FromResult<LogicNewBlockDraft?>(new LogicNewBlockDraft("Dry end stop", "Primary Arm", "DRYEND"));
            Assert.True(await vm.Logic.StartNewBlockAsync());

            LogicTagEntryViewModel shared = Assert.Single(vm.Logic.AreaTags.Entries);
            Assert.Equal("START_PB", shared.Name);
            Assert.True(shared.IsAssigned);
            Assert.Equal(1, vm.Logic.AreaTags.UsedByCount);

            // The verdict is unchanged: the same boundary tag, now read through the area's tags.
            using JsonDocument state = await handle.GetJsonAsync("/api/logic/state");
            JsonElement blockState = state.RootElement.GetProperty("blocks")[0];
            Assert.Equal("unknown", blockState.GetProperty("state").GetString());
            Assert.Equal("no data for Line 01 Start Permit", blockState.GetProperty("reason").GetString());
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task GivingALegacyBlockAnAreaConvertsItsDirectTags()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        await SaveBlockAsync(handle, Block("Dry end start", ""));

        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);
            vm.Logic.SelectBlockCommand.Execute(Row(vm, "Dry end start"));
            Assert.Empty(vm.Logic.AreaTags.Entries);

            // Assigning the area converts: the block's direct tags become the area's entries.
            vm.Logic.BlockArea = "DRYEND";

            LogicTagEntryViewModel entry = Assert.Single(vm.Logic.AreaTags.Entries);
            Assert.Equal("LINE_01_START_PERMIT", entry.Name);
            Assert.True(entry.UsedByThisBlock);
            Assert.Contains("Moved 1 tag", vm.Logic.EditorMessage);
            LogicConditionRowViewModel row = Assert.Single(vm.Logic.Conditions);
            Assert.Equal("LINE_01_START_PERMIT", row.Tag);
            Assert.False(row.HasStoredTag);

            await vm.Logic.SaveCommand.ExecuteAsync(null);

            using JsonDocument document = await handle.GetJsonAsync("/api/logic");
            Assert.Equal("DRYEND", document.RootElement.GetProperty("tagSources")[0].GetProperty("name").GetString());
            JsonElement stored = document.RootElement.GetProperty("blocks")[0];
            Assert.Equal("DRYEND", stored.GetProperty("tagSource").GetString());
            Assert.Equal("LINE_01_START_PERMIT", stored.GetProperty("conditions")[0].GetProperty("tag").GetString());
            Assert.Equal(string.Empty, stored.GetProperty("conditions")[0].GetProperty("itemId").GetString());
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task OpeningABlockWithAnAreaMovesItsDirectTagsIntoTheAreasTags()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        LogicBlockDto leftover = Block("Dry end start", "");
        leftover.Area = "DRYEND"; // an area with a direct pair left over from before
        await SaveBlockAsync(handle, leftover);

        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);
            vm.Logic.SelectBlockCommand.Execute(Row(vm, "Dry end start"));

            // The list is the area's, so the pair the block still carried joins it as the draft
            // opens: one entry, named after the tag, and the row reads it from the first moment.
            LogicTagEntryViewModel entry = Assert.Single(vm.Logic.AreaTags.Entries);
            Assert.Equal("LINE_01_START_PERMIT", entry.Name);
            Assert.Equal("ns=2;s=Status/Line01.Permit", entry.ItemId);
            Assert.True(entry.UsedByThisBlock);
            Assert.Contains("Moved 1 tag", vm.Logic.EditorMessage);

            LogicConditionRowViewModel row = Assert.Single(vm.Logic.Conditions);
            Assert.Equal("LINE_01_START_PERMIT", row.Tag);
            Assert.False(row.HasStoredTag);

            // The move is unsaved — the bridge's copy still holds the direct pair — so Save stores it.
            Assert.True(vm.Logic.IsDirty);
            await vm.Logic.SaveCommand.ExecuteAsync(null);

            Assert.Equal("✓ Saved.", vm.Logic.EditorMessage);
            Assert.False(vm.Logic.IsDirty);

            using JsonDocument document = await handle.GetJsonAsync("/api/logic");
            Assert.Equal(
                "LINE_01_START_PERMIT",
                document.RootElement.GetProperty("tagSources")[0].GetProperty("tags")[0].GetProperty("name").GetString());
            JsonElement condition = document.RootElement.GetProperty("blocks")[0].GetProperty("conditions")[0];
            Assert.Equal("LINE_01_START_PERMIT", condition.GetProperty("tag").GetString());
            Assert.Equal(string.Empty, condition.GetProperty("itemId").GetString());
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task ALegacyPairWithNoAreaIsShownAndKeptThroughASave()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        await SaveBlockAsync(handle, Block("Dry end start", "")); // a direct pair, no area and no group

        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);
            vm.Logic.SelectBlockCommand.Execute(Row(vm, "Dry end start"));

            // There is no list to move the pair into, so the row shows what it reads and the editor
            // points at the Tags tab — the pair is kept, never dropped behind the operator's back.
            LogicConditionRowViewModel row = Assert.Single(vm.Logic.Conditions);
            Assert.False(row.HasBlockTag);
            Assert.True(row.HasStoredTag);
            Assert.Contains("Line 01 Start Permit", row.StoredTagText);
            Assert.Empty(vm.Logic.AreaTags.Entries);
            Assert.Contains("Tags tab", vm.Logic.AreaTagsPrompt);

            row.Text = "Dry end start permit must still be given";
            Assert.True(vm.Logic.IsDirty);
            await vm.Logic.SaveCommand.ExecuteAsync(null);

            Assert.Equal("✓ Saved.", vm.Logic.EditorMessage);
            using JsonDocument document = await handle.GetJsonAsync("/api/logic");
            Assert.Empty(document.RootElement.GetProperty("tagSources").EnumerateArray());
            JsonElement condition = document.RootElement.GetProperty("blocks")[0].GetProperty("conditions")[0];
            Assert.Equal("Dry end start permit must still be given", condition.GetProperty("text").GetString());
            Assert.Equal(string.Empty, condition.GetProperty("tag").GetString());
            Assert.Equal("ns=2;s=Status/Line01.Permit", condition.GetProperty("itemId").GetString());
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task NewBlockAsksForTheAreaAndOpensTheDraftWithIt()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);

            LogicNewBlockRequest? asked = null;
            vm.Logic.NewBlockPrompt = (_, request) =>
            {
                asked = request;
                return Task.FromResult<LogicNewBlockDraft?>(
                    new LogicNewBlockDraft("Dry end start", "Primary Arm", "DRYEND"));
            };

            Assert.True(await vm.Logic.StartNewBlockAsync());
            Assert.NotNull(asked);
            Assert.Equal("Dry end start", vm.Logic.BlockName);
            Assert.Equal("Primary Arm", vm.Logic.BlockGroup);
            Assert.Equal("DRYEND", vm.Logic.BlockArea);
            Assert.Equal("DRYEND", vm.Logic.AreaTags.Name); // the area is set before any tag

            // A cancelled dialog starts nothing and keeps the draft.
            vm.Logic.NewBlockPrompt = (_, _) => Task.FromResult<LogicNewBlockDraft?>(null);
            Assert.False(await vm.Logic.StartNewBlockAsync());
            Assert.Equal("Dry end start", vm.Logic.BlockName);

            // The next dialog is offered what the current draft carries, so siblings are quick.
            vm.Logic.NewBlockPrompt = (_, request) =>
            {
                asked = request;
                return Task.FromResult<LogicNewBlockDraft?>(null);
            };
            Assert.False(await vm.Logic.StartNewBlockAsync());
            Assert.Equal("Primary Arm", asked!.Group);
            Assert.Equal("DRYEND", asked.Area);
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task SwitchingTheAreaKeepsEveryRowsReferenceResolvable()
    {
        await using TestAppHandle handle = await StartAsync(PermitTag());
        LogicBlockDto block = Block("Dry end start", "");
        block.Area = "DRYEND";
        block.Conditions[0].Tag = "START_PB";
        block.Conditions[0].SourceId = string.Empty;
        block.Conditions[0].ItemId = string.Empty;
        await SaveBlockAsync(handle, block);
        await SaveAreaTagsAsync(handle, "DRYEND", new LogicTagDto
        {
            Name = "START_PB",
            SourceId = "sim",
            ItemId = "ns=2;s=Status/Line01.Permit"
        });
        await SaveAreaTagsAsync(handle, "WETEND", new LogicTagDto { Name = "OTHER" });

        MainViewModel vm = NewViewModel(out string configPath);
        try
        {
            await ConnectAsync(vm, handle);
            vm.Logic.SelectBlockCommand.Execute(Row(vm, "Dry end start"));
            LogicConditionRowViewModel row = Assert.Single(vm.Logic.Conditions);
            Assert.Equal("START_PB", row.Tag);
            Assert.True(row.SelectedBlockTag!.IsAssigned);

            // Switching the area adopts the new list — the row's reference is added back with its
            // binding instead of dangling (and its picker stays on the entry).
            vm.Logic.BlockArea = "WETEND";

            Assert.Equal("WETEND", vm.Logic.AreaTags.Name);
            Assert.Contains(vm.Logic.AreaTags.Entries, entry => entry.Name == "OTHER");
            LogicTagEntryViewModel kept = Assert.Single(vm.Logic.AreaTags.Entries, entry => entry.Name == "START_PB");
            Assert.True(kept.IsAssigned);
            Assert.Equal("ns=2;s=Status/Line01.Permit", kept.ItemId);
            Assert.Equal("START_PB", row.Tag);
            Assert.Same(kept, row.SelectedBlockTag);
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    private static async Task SaveAreaTagsAsync(TestAppHandle handle, string name, LogicTagDto tag)
    {
        using HttpResponseMessage response = await handle.Client.PostAsJsonAsync(
            "/api/logic/tagsources",
            new { tagSource = new LogicAreaTagsDto { Name = name, Tags = { tag } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
