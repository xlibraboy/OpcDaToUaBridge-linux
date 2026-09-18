using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>
/// One saved trend group on the trend-groups page: its editable name plus the tag list the page
/// shows. Rows carry tag metadata only (never a live value) and are refreshed on demand — after
/// edits, or when the bridge mappings change — so the page does not churn with the value stream.
/// </summary>
public partial class TrendGroupItemViewModel : ObservableObject
{
    public TrendGroupItemViewModel(TrendGroupDefinition definition)
    {
        Definition = definition;
        _name = definition.Name;
    }

    public TrendGroupDefinition Definition { get; }

    /// <summary>Group name; the page saves it when the name box loses focus.</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>The group's tags in saved order, as metadata-only rows.</summary>
    public ObservableCollection<TagListRow> Tags { get; } = new();

    /// <summary>Tag count line under the name, plus a note when tags are no longer on the bridge.</summary>
    public string Subtitle
    {
        get
        {
            string count = Tags.Count == 1 ? "1 tag" : $"{Tags.Count} tags";
            int missing = Tags.Count(row => row.HasMarker);
            return missing == 0 ? count : $"{count} · {missing} not on bridge";
        }
    }

    /// <summary>
    /// Rebuilds the tag rows from the saved definitions and the live tag cache (for display names).
    /// Tags that are gone stay in the group, marked "not on bridge", so they return with the bridge.
    /// </summary>
    public void RefreshRows(Func<TagBindingKey, MultiBridgeTagEntry?> lookup)
    {
        Tags.Clear();
        foreach (TrendGroupPenDefinition pen in Definition.Pens)
        {
            MultiBridgeTagEntry? entry = lookup(pen.Key);
            Tags.Add(new TagListRow(
                pen.Key,
                pen.BridgeId,
                string.IsNullOrWhiteSpace(entry?.SourceName) ? pen.SourceId : entry!.SourceName,
                string.IsNullOrWhiteSpace(entry?.DisplayName) ? pen.DaItemId : entry!.DisplayName,
                pen.DaItemId,
                entry is null ? "not on bridge" : string.Empty));
        }

        OnPropertyChanged(nameof(Subtitle));
    }

    /// <summary>Adds a tag to the group when it is not in it yet; returns false for a duplicate.</summary>
    public bool AddTag(TagBindingKey key)
    {
        if (Definition.Pens.Any(pen => pen.Key.EqualsIgnoreCase(key)))
        {
            return false;
        }

        Definition.Pens.Add(new TrendGroupPenDefinition
        {
            BridgeId = key.BridgeId,
            SourceId = key.SourceId,
            DaItemId = key.DaItemId
        });
        return true;
    }

    /// <summary>Removes a tag from the group; returns false when it was not a member.</summary>
    public bool RemoveTag(TagBindingKey key)
    {
        TrendGroupPenDefinition? pen = Definition.Pens.FirstOrDefault(p => p.Key.EqualsIgnoreCase(key));
        return pen is not null && Definition.Pens.Remove(pen);
    }

    partial void OnNameChanged(string value) => Definition.Name = value;
}
