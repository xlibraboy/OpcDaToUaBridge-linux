using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Designer.Services;
using OpcBridge.Hmi.Designer.ViewModels;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>The Designer's tag picker: mapped tags of one source, filtered, with live values.</summary>
public sealed class DesignerTagPickerViewModelTests
{
    private static MultiBridgeTagCache Cache(params (string SourceId, string ItemId, string DisplayName, string? Description)[] tags)
    {
        var cache = new MultiBridgeTagCache();
        cache.ReplaceBridge(
            "default",
            tags.Select(tag => new HmiTagDto
            {
                SourceId = tag.SourceId,
                SourceName = tag.SourceId.ToUpperInvariant(),
                SourceType = "OpcDa",
                ItemId = tag.ItemId,
                DisplayName = tag.DisplayName,
                Description = tag.Description,
                DataType = "Double",
                Value = 12.5,
                IsGood = true,
                DaQuality = 192
            }).ToArray());
        return cache;
    }

    private static BridgeSourceInfo Source(string sourceId) => new(sourceId, sourceId.ToUpperInvariant(), "OpcDa", "", "Connected");

    [Fact]
    public void Rows_AreScopedToTheSelectedSource()
    {
        var cache = Cache(
            ("s1", "Tank.Level", "Tank level", null),
            ("s2", "Flow", "Flow rate", null));
        var vm = new DesignerTagPickerViewModel(new[] { Source("s1"), Source("s2") }, cache, "s2");

        DesignerTagRow row = Assert.Single(vm.Rows);
        Assert.Equal("Flow rate", row.DisplayName);
        Assert.Equal("Flow", row.ItemId);
        Assert.Equal("s2", vm.SelectedSource!.SourceId);
        Assert.True(vm.CanBind);
    }

    [Fact]
    public void Filter_MatchesDisplayName_ItemId_AndDescription()
    {
        var cache = Cache(
            ("s1", "Tank.Level", "Tank level", "Level of tank 1"),
            ("s1", "Tank.Flow", "Tank flow", "Outlet flow"),
            ("s1", "Pump.Speed", "Pump speed", null));
        var vm = new DesignerTagPickerViewModel(new[] { Source("s1") }, cache, "s1");

        vm.Filter = "flow";
        Assert.Single(vm.Rows);
        Assert.Equal("Tank.Flow", vm.Rows[0].ItemId);

        vm.Filter = "outlet";
        Assert.Single(vm.Rows);
        Assert.Equal("Tank.Flow", vm.Rows[0].ItemId);

        vm.Filter = "pump";
        Assert.Single(vm.Rows);
        Assert.Equal("Pump.Speed", vm.Rows[0].ItemId);
    }

    [Fact]
    public void Confirm_ReturnsTheSelectedKey()
    {
        var cache = Cache(("s1", "Tank.Level", "Tank level", null));
        var vm = new DesignerTagPickerViewModel(new[] { Source("s1") }, cache, "s1");

        Assert.Null(vm.Result);
        vm.Confirm();

        Assert.NotNull(vm.Result);
        Assert.Equal("default", vm.Result!.Value.BridgeId);
        Assert.Equal("s1", vm.Result.Value.SourceId);
        Assert.Equal("Tank.Level", vm.Result.Value.DaItemId);
    }

    [Fact]
    public void RefreshValues_PicksUpCacheDeltas()
    {
        var cache = Cache(("s1", "Tank.Level", "Tank level", null));
        var vm = new DesignerTagPickerViewModel(new[] { Source("s1") }, cache, "s1");
        Assert.Equal("12.5", vm.Rows[0].ValueText);

        cache.ApplyDeltas("default", new[]
        {
            new HmiValueDelta
            {
                SourceId = "s1",
                ItemId = "Tank.Level",
                Value = 99.25,
                TimestampUtc = DateTime.UtcNow,
                DaQuality = 192,
                IsGood = true
            }
        });
        vm.RefreshLive();

        Assert.Equal("99.25", vm.Rows[0].ValueText);
        Assert.Equal("Good (192)", vm.Rows[0].QualityText);
    }

    [Fact]
    public void EmptyMessage_ExplainsWhenTheSourceHasNoMappedTags()
    {
        var cache = Cache(("s1", "Tank.Level", "Tank level", null));
        var vm = new DesignerTagPickerViewModel(
            new[] { Source("s1"), new BridgeSourceInfo("s2", "Line 2", "OpcDa", "", null) },
            cache,
            "s2");

        Assert.Empty(vm.Rows);
        Assert.False(vm.CanBind);
        Assert.Contains("No mapped tags", vm.EmptyMessage, StringComparison.Ordinal);

        var noSources = new DesignerTagPickerViewModel(Array.Empty<BridgeSourceInfo>(), cache, null);
        Assert.Contains("No sources", noSources.EmptyMessage, StringComparison.Ordinal);
    }
}
