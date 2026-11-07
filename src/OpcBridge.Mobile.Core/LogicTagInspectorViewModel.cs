using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Client;
using OpcBridge.Core;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Mobile.Core;

/// <summary>One line of the tag inspector: a label/value pair, or a warning.</summary>
public sealed partial class LogicTagRowViewModel : ObservableObject
{
    [ObservableProperty] private string _value;
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>The raw bit/number drawn by the LIVE row, before the STATE chip.</summary>
    [ObservableProperty] private string _rawValue = "—";

    /// <summary>The mapping's operator word drawn in the LIVE row's STATE chip.</summary>
    [ObservableProperty] private string _stateWord = string.Empty;

    public LogicTagRowViewModel(string key, string label, string value, bool isWarning = false, bool isLive = false)
    {
        Key = key;
        Label = label;
        _value = value;
        IsWarning = isWarning;
        IsLive = isLive;
    }

    public string Key { get; }

    public string Label { get; }

    public bool IsWarning { get; }

    public bool IsLive { get; }

    public bool IsPair => !IsWarning;

    /// <summary>A pair renders its plain value unless the LIVE row draws raw + STATE instead.</summary>
    public bool ShowPlainValue => IsPair && !IsLive;

    public bool ShowStateWord => IsLive && StateWord.Length > 0;

    partial void OnStateWordChanged(string value) => OnPropertyChanged(nameof(ShowStateWord));

    public void ApplyLive(string raw, string word)
    {
        RawValue = raw;
        StateWord = word;
        OnPropertyChanged(nameof(ShowStateWord));
    }
}

/// <summary>One block behind a tag, as the inspector's card: who it is, what it makes of the plant, and why it references the tag.</summary>
public sealed partial class LogicTagUseViewModel : ObservableObject
{
    [ObservableProperty] private string _stateLabel = "NO DATA";
    [ObservableProperty] private string _stateKey = LogicBlockStates.Unknown;

    public LogicTagUseViewModel(LogicBlockDto block, IReadOnlyList<string> lines)
    {
        BlockId = block.Id;
        Name = block.Name;
        Lines = lines;
        Meta = MetaOf(block);
    }

    public Guid BlockId { get; }

    public string Name { get; }

    /// <summary>"Interlock · DRY END · Primary Arm" — the card's second line.</summary>
    public string Meta { get; }

    /// <summary>The lines that read or write the tag (a contact's sentence, a step's handshake, an action).</summary>
    public IReadOnlyList<string> Lines { get; }

    public void ApplyState(LogicBlockStateDto? state)
    {
        StateKey = state?.State ?? LogicBlockStates.Unknown;
        StateLabel = StateKey switch
        {
            LogicBlockStates.Ready => "READY",
            LogicBlockStates.Blocked => "BLOCKED",
            LogicBlockStates.Disabled => "DISABLED",
            _ => "NO DATA"
        };
    }

    private static string MetaOf(LogicBlockDto block)
    {
        List<string> parts = new()
        {
            block.Kind switch
            {
                LogicBlockKinds.Sequence => "Sequence",
                LogicBlockKinds.Permissive => "Permissive",
                _ => "Interlock"
            }
        };
        if (!string.IsNullOrWhiteSpace(block.Area))
        {
            parts.Add(block.Area.Trim());
        }

        if (!string.IsNullOrWhiteSpace(block.Group))
        {
            parts.Add(block.Group.Trim());
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>
/// The Result row's tag door: where the tag comes from (its area entry, device contact, mapped
/// source and live reading) and which blocks read, write or drive it. Mirrors the web Result
/// tab's tag inspector: a Source view and a Used by view, switched by the sheet's own tabs.
/// </summary>
public sealed partial class LogicTagInspectorViewModel : ObservableObject
{
    private readonly LogicTagReference reference_;
    private readonly LogicTagUsageIndex usage_;
    private readonly LogicTagDto? areaEntry_;
    private readonly Dictionary<string, LogicTagRowViewModel> rows_ = new(StringComparer.Ordinal);
    private MultiBridgeTagEntry? tagEntry_;

    [ObservableProperty] private bool _isSourceView = true;

    public LogicTagInspectorViewModel(
        LogicTagReference reference,
        LogicAreaTagsDto? areaTags,
        LogicTagUsageIndex usage,
        MultiBridgeTagCache? tags,
        string bridgeKey,
        IReadOnlyDictionary<Guid, LogicBlockStateDto>? states = null)
    {
        reference_ = reference;
        usage_ = usage;
        areaEntry_ = reference.Named ? LogicAreaTags.FindTag(areaTags, reference.Name) : null;
        tagEntry_ = ResolveEntry(tags, bridgeKey);

        Title = TagTitle();
        Subtitle = reference.Named
            ? (reference.Area.Length > 0 ? "Area " + reference.Area : "No area")
            : "Direct tag pair";

        foreach (LogicTagUsage use in usage.Drivers(reference))
        {
            Drivers.Add(new LogicTagUseViewModel(use.Block, use.Lines));
        }

        foreach (LogicTagUsage use in usage.Readers(reference))
        {
            Readers.Add(new LogicTagUseViewModel(use.Block, use.Lines));
        }

        AddIdentityRows();
        AddMappedRows();
        ApplyStates(states);
    }

    /// <summary>The identity the page uses to resolve live values for the open inspector.</summary>
    public LogicTagReference Reference => reference_;

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>The Source tab's tag identity and area-entry rows.</summary>
    public ObservableCollection<LogicTagRowViewModel> IdentityRows { get; } = new();

    /// <summary>The MAPPED SOURCE rows, including the live raw reading and its state chip.</summary>
    public ObservableCollection<LogicTagRowViewModel> MappedRows { get; } = new();

    /// <summary>Blocks whose output/coil drives the tag.</summary>
    public ObservableCollection<LogicTagUseViewModel> Drivers { get; } = new();

    /// <summary>Blocks that read or write the tag (the output role does not count as a read).</summary>
    public ObservableCollection<LogicTagUseViewModel> Readers { get; } = new();

    public string DriverSummary => Drivers.Count > 0
        ? Drivers.Count + (Drivers.Count == 1 ? " block drives this tag" : " blocks drive this tag")
            + " — open one for its Result."
        : reference_.IsBound
            ? "No block drives this tag — it comes from the mapped source below."
            : "No block drives this tag.";

    /// <summary>"Used by (2)" — the second tab's own count.</summary>
    public string UsageTabLabel => "Used by (" + Readers.Count + ")";

    public string UsageSummary => Readers.Count > 0
        ? Readers.Count + (Readers.Count == 1 ? " block uses this tag" : " blocks use this tag")
            + " — open one for its Result."
        : "No block reads this tag.";

    public bool IsUsageView => !IsSourceView;

    partial void OnIsSourceViewChanged(bool value) => OnPropertyChanged(nameof(IsUsageView));

    public void ShowSource() => IsSourceView = true;

    public void ShowUsage() => IsSourceView = false;

    /// <summary>Updates the live/source rows when a value delta arrives, without rebuilding the sheet.</summary>
    public void ApplyEntry(MultiBridgeTagEntry? entry)
    {
        tagEntry_ = entry;
        SetRow("source", SourceLabel());
        SetOptionalRow("source-type", Optional(entry?.SourceType));
        SetRow("display", Optional(entry?.DisplayName));
        SetRow("type", Optional(entry?.DataType));
        SetOptionalRow("unit", Optional(entry?.Unit));
        SetLive(entry);
        SetRow("quality", Quality(entry));
        SetOptionalRow("rate", Rate(entry));
        SetRow("writable", Writable(entry));
        string form = ContactForm(areaEntry_);
        if (form is "no" or "nc")
        {
            SetRow("field", FieldState(form, entry));
        }

        LogicTagRowViewModel? missing = MappedRows.FirstOrDefault(row => row.Key == "no-mapped-tag");
        if (entry is not null && missing is not null)
        {
            MappedRows.Remove(missing);
            rows_.Remove(missing.Key);
        }
        else if (entry is null && reference_.IsBound && missing is null)
        {
            AddWarning(MappedRows, "no-mapped-tag", "The bridge has no mapped tag for " + PairText() + ".");
        }
    }

    public void ApplyStates(IReadOnlyDictionary<Guid, LogicBlockStateDto>? states)
    {
        foreach (LogicTagUseViewModel use in Drivers.Concat(Readers))
        {
            use.ApplyState(states is not null && states.TryGetValue(use.BlockId, out LogicBlockStateDto? state)
                ? state
                : null);
        }
    }

    private void AddIdentityRows()
    {
        if (reference_.Named)
        {
            AddRow(IdentityRows, "tag", "Tag", reference_.Name);
            AddRow(IdentityRows, "area", "Area", reference_.Area.Length > 0 ? reference_.Area : "—");
            string form = ContactForm(areaEntry_);
            AddRow(IdentityRows, "contact", "Device contact", form switch
            {
                "no" => "NO",
                "nc" => "NC",
                _ => "not recorded"
            });
            if (form is "no" or "nc")
            {
                AddRow(IdentityRows, "field", "Field", FieldState(form, tagEntry_));
            }

            if (reference_.Area.Length == 0)
            {
                AddWarning(IdentityRows, "no-area", "This block has no area — the name cannot resolve to a source.");
            }
            else if (areaEntry_ is null)
            {
                AddWarning(IdentityRows, "no-entry", "No tag named " + reference_.Name + " in " + reference_.Area + " — add it on the Tags tab.");
            }
            else if (!reference_.IsBound && Drivers.Count == 0)
            {
                AddWarning(IdentityRows, "unassigned", reference_.Name + " is not assigned a source and tag yet — assign it on the Tags tab.");
            }
        }
        else
        {
            AddRow(IdentityRows, "tag", "Tag", Title);
        }
    }

    private void AddMappedRows()
    {
        AddRow(MappedRows, "source", "Source", SourceLabel());
        AddRow(MappedRows, "source-id", "Source id", reference_.SourceId.Length > 0 ? reference_.SourceId : "—");
        AddOptionalRow(MappedRows, "source-type", "Source type", Optional(tagEntry_?.SourceType));
        AddRow(MappedRows, "item", "Item", reference_.ItemId.Length > 0 ? reference_.ItemId : "—");
        AddRow(MappedRows, "display", "Display", Optional(tagEntry_?.DisplayName));
        AddRow(MappedRows, "type", "Type", Optional(tagEntry_?.DataType));
        AddOptionalRow(MappedRows, "unit", "Unit", Optional(tagEntry_?.Unit));
        LogicTagRowViewModel live = AddRow(MappedRows, "live", "Live", LogicTagText.Format(tagEntry_), isLive: true);
        live.ApplyLive(LogicTagText.Raw(tagEntry_), LogicTagText.StateWord(tagEntry_));
        AddRow(MappedRows, "quality", "Quality", Quality(tagEntry_));
        AddOptionalRow(MappedRows, "rate", "Rate", Rate(tagEntry_));
        AddRow(MappedRows, "writable", "Writable", Writable(tagEntry_));

        if (reference_.IsBound && tagEntry_ is null)
        {
            AddWarning(MappedRows, "no-mapped-tag", "The bridge has no mapped tag for " + PairText() + ".");
        }
    }

    private LogicTagRowViewModel AddRow(
        ObservableCollection<LogicTagRowViewModel> target,
        string key,
        string label,
        string value,
        bool isLive = false)
    {
        LogicTagRowViewModel row = new(key, label, value, isLive: isLive);
        rows_[key] = row;
        target.Add(row);
        return row;
    }

    private LogicTagRowViewModel AddOptionalRow(
        ObservableCollection<LogicTagRowViewModel> target,
        string key,
        string label,
        string value)
    {
        LogicTagRowViewModel row = AddRow(target, key, label, value);
        row.IsVisible = value != "—";
        return row;
    }

    private void AddWarning(ObservableCollection<LogicTagRowViewModel> target, string key, string text)
    {
        LogicTagRowViewModel row = new(key, string.Empty, text, isWarning: true);
        rows_[key] = row;
        target.Add(row);
    }

    private void SetRow(string key, string value)
    {
        if (rows_.TryGetValue(key, out LogicTagRowViewModel? row))
        {
            row.Value = value;
        }
    }

    private void SetOptionalRow(string key, string value)
    {
        if (rows_.TryGetValue(key, out LogicTagRowViewModel? row))
        {
            row.Value = value;
            row.IsVisible = value != "—";
        }
    }

    private void SetLive(MultiBridgeTagEntry? entry)
    {
        if (!rows_.TryGetValue("live", out LogicTagRowViewModel? row))
        {
            return;
        }

        row.Value = LogicTagText.Format(entry);
        row.ApplyLive(LogicTagText.Raw(entry), LogicTagText.StateWord(entry));
    }

    private MultiBridgeTagEntry? ResolveEntry(MultiBridgeTagCache? tags, string bridgeKey)
    {
        if (tags is null || !reference_.IsBound)
        {
            return null;
        }

        return tags.TryGet(
            TagBindingKey.Create(bridgeKey, reference_.SourceId, reference_.ItemId),
            out MultiBridgeTagEntry? entry)
            ? entry
            : null;
    }

    private string TagTitle()
    {
        if (reference_.Named)
        {
            return reference_.Name;
        }

        string display = (tagEntry_?.DisplayName ?? string.Empty).Trim();
        if (display.Length > 0)
        {
            return display;
        }

        return reference_.ItemId.Length > 0 ? reference_.ItemId : reference_.SourceId;
    }

    private string SourceLabel()
    {
        string name = (tagEntry_?.SourceName ?? string.Empty).Trim();
        if (name.Length > 0)
        {
            return name;
        }

        return reference_.SourceId.Length > 0 ? reference_.SourceId : "not assigned";
    }

    private string PairText() =>
        (reference_.SourceId.Length > 0 ? reference_.SourceId : "(no source)") + " / " + reference_.ItemId;

    private static string Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string Quality(MultiBridgeTagEntry? entry) => entry?.IsGood switch
    {
        true => "good",
        false => "bad",
        _ => "—"
    };

    private static string Rate(MultiBridgeTagEntry? entry) =>
        entry is { UpdateRateMs: > 0 } ? entry.UpdateRateMs + " ms" : "—";

    private static string Writable(MultiBridgeTagEntry? entry) =>
        entry is null ? "—" : entry.Writeable ? "yes" : "no";

    private static string ContactForm(LogicTagDto? entry)
    {
        string form = (entry?.Contact ?? string.Empty).Trim().ToLowerInvariant();
        return form is LogicContactForms.NormallyOpen or LogicContactForms.NormallyClosed
            ? form
            : string.Empty;
    }

    /// <summary>
    /// The field device's own reading: an NO device is actuated at tag 1, an NC device at 0.
    /// A bad or missing value reads "—", never a guessed state.
    /// </summary>
    private static string FieldState(string form, MultiBridgeTagEntry? entry)
    {
        if (entry is null || entry.IsGood == false || entry.Value is null)
        {
            return "—";
        }

        bool bit = TagDigital.CoerceBool(entry.Value);
        bool actuated = form == LogicContactForms.NormallyClosed ? !bit : bit;
        return actuated ? "actuated" : "at rest";
    }
}
