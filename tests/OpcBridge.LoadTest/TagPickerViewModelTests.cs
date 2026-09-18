using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.ViewModels;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class TagPickerViewModelTests
{
    [Fact]
    public void Filter_MatchesNamesAndIds_CaseInsensitively()
    {
        TagPickerViewModel picker = NewPicker(
            false,
            Row("Tank1.Level", "Level of Tank 1"),
            Row("Pump1.Run", "Pump 1 running", sourceName: "melsec-line2", bridgeId: "line2"));

        picker.Filter = "level of tank";
        Assert.Equal("Tank1.Level", Assert.Single(picker.Rows).DaItemId);

        picker.Filter = "LINE2";
        Assert.Equal("Pump1.Run", Assert.Single(picker.Rows).DaItemId);

        picker.Filter = "melsec";
        Assert.Equal("Pump1.Run", Assert.Single(picker.Rows).DaItemId);

        picker.Filter = "pump1.run";
        Assert.Equal("Pump1.Run", Assert.Single(picker.Rows).DaItemId);

        picker.Filter = "nothing matches this";
        Assert.Empty(picker.Rows);

        picker.Filter = string.Empty;
        Assert.Equal(2, picker.Rows.Count);
    }

    [Fact]
    public void Rows_AreTheSameSnapshotInstances_AcrossFilterChanges()
    {
        TagListRow row = Row("Tank1.Level", "Level of Tank 1");
        TagPickerViewModel picker = NewPicker(false, row, Row("Pump1.Run", "Pump 1 running"));

        picker.Filter = "tank";
        Assert.Same(row, Assert.Single(picker.Rows));

        picker.Filter = string.Empty;
        Assert.Same(row, picker.Rows[0]);
    }

    [Fact]
    public void CanConfirm_RequiresASelection_AndANameWhenAsked()
    {
        TagPickerViewModel picker = NewPicker(false, Row("Tank1.Level", "Level of Tank 1"));
        Assert.False(picker.CanConfirm);

        picker.SetSelectedCount(1);
        Assert.True(picker.CanConfirm);

        picker.SetSelectedCount(0);
        Assert.False(picker.CanConfirm);

        TagPickerViewModel creator = NewPicker(true, Row("Tank1.Level", "Level of Tank 1"));
        creator.SetSelectedCount(1);
        Assert.False(creator.CanConfirm);

        creator.GroupName = "   ";
        Assert.False(creator.CanConfirm);

        creator.GroupName = "Boiler";
        Assert.True(creator.CanConfirm);
    }

    [Fact]
    public void AlreadyAddedTags_AreListedDisabledWithTheInGroupMarker()
    {
        TagPickerViewModel picker = NewPicker(
            false,
            Row("Tank1.Level", "Level of Tank 1", marker: "in group", enabled: false),
            Row("Pump1.Run", "Pump 1 running"));

        Assert.False(picker.Rows[0].Enabled);
        Assert.Equal("in group", picker.Rows[0].Marker);
        Assert.True(picker.Rows[0].HasMarker);
        Assert.True(picker.Rows[1].Enabled);
        Assert.False(picker.Rows[1].HasMarker);
    }

    [Fact]
    public void Rows_CarryNoLiveValue()
    {
        // The picker lists tags to choose from — a live value/quality/timestamp must never be
        // displayed here, so the row type must not carry one at all.
        string[] forbidden = { "Value", "ValueText", "Timestamp", "TimestampText", "Quality", "QualityText" };
        string[] members = typeof(TagListRow).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain(members, name => forbidden.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    private static TagPickerViewModel NewPicker(bool askForName, params TagListRow[] rows) =>
        new(rows, "Group trend", "Open trend", askForName);

    private static TagListRow Row(
        string itemId,
        string displayName,
        string sourceName = "ua-sim",
        string bridgeId = "default",
        string marker = "",
        bool enabled = true) =>
        new(
            TagBindingKey.Create(bridgeId, "src", itemId),
            bridgeId,
            sourceName,
            displayName,
            itemId,
            marker,
            enabled);
}
