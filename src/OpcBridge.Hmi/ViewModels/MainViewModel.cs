using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;
using OpcBridge.Hmi.Views;

namespace OpcBridge.Hmi.ViewModels;

public enum HmiPage
{
    Home,
    Config,
    Trends
}

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly BridgeConnectionManager connections_;
    private readonly Dictionary<string, DisplayStoreClient> storeClients_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly PopupWindowService popups_;
    private readonly Dictionary<TagBindingKey, TagItemViewModel> tagIndex_ = new(TagBindingKeyComparer.Instance);
    private MultiBridgeTagEntry[] tagEntries_ = Array.Empty<MultiBridgeTagEntry>();
    private readonly Dictionary<string, bool> bridgeInfluxConnected_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool ownsServices_;
    private CancellationTokenSource? connectCts_;
    private Window? ownerWindow_;
    private readonly List<FaceplateViewModel> openFaceplates_ = new();
    private readonly string configPath_;
    private readonly string trendGroupsPath_;

    public MainViewModel()
        : this(new BridgeConnectionManager(), new PopupWindowService(), ownsServices: true)
    {
    }

    public MainViewModel(
        BridgeConnectionManager connections,
        PopupWindowService popups,
        bool ownsServices = false,
        string? configPath = null)
    {
        connections_ = connections;
        popups_ = popups;
        ownsServices_ = ownsServices;
        configPath_ = string.IsNullOrWhiteSpace(configPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OpcBridge.Hmi",
                "hmi-config.json")
            : configPath!;
        trendGroupsPath_ = TrendGroupStore.DefaultPath(configPath_);
        DisplaySurface = new DisplaySurfaceViewModel(
            connections_.Cache,
            OpenFaceplateFor,
            WriteForBindingAsync);
        connections_.CacheChanged += OnCacheChanged;
        connections_.MappingsChanged += OnMappingsChangedAsync;
        connections_.InfluxStatusChanged += OnInfluxStatusChanged;
        DisplaySurface.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DisplaySurfaceViewModel.HasDocument) or nameof(DisplaySurfaceViewModel.DisplayName))
            {
                OnPropertyChanged(nameof(HasDisplay));
                OnPropertyChanged(nameof(DisplayTitle));
            }
        };
        LoadLocalConfig();
        LoadTrendGroups();
        _ = DetectLocalBridgeAsync();
    }

    private async Task DetectLocalBridgeAsync()
    {
        string? found = await LocalBridgeDetector.DetectAsync().ConfigureAwait(true);
        await PostToUiAsync(() =>
        {
            BridgeRow? empty = BridgeRows.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.Address));
            if (found is not null && empty is not null && !IsConnected)
            {
                empty.Address = found;
                RefreshPrimaryAddress();
                StatusMessage = $"Local OpcBridge detected at {found}";
            }
        });
    }

    private BridgeRow AddBridgeRow(string name = "", string address = "", string displayStore = "")
    {
        var row = new BridgeRow { Name = name, Address = address, DisplayStore = displayStore };
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BridgeRow.Address) && BridgeRows.Count > 0 && ReferenceEquals(BridgeRows[0], row))
            {
                RefreshPrimaryAddress();
            }
        };
        BridgeRows.Add(row);
        return row;
    }

    private void RefreshPrimaryAddress()
    {
        BaseUrl = BridgeRows.Count > 0 ? BridgeRows[0].Address : string.Empty;
    }

    public DisplaySurfaceViewModel DisplaySurface { get; }

    // ---- Page navigation (SCADA shell) ----

    [ObservableProperty]
    private HmiPage _currentPage = HmiPage.Home;

    public bool IsHomePage => CurrentPage == HmiPage.Home;

    public bool IsConfigPage => CurrentPage == HmiPage.Config;

    /// <summary>Saved trend groups page.</summary>
    public bool IsTrendsPage => CurrentPage == HmiPage.Trends;

    partial void OnCurrentPageChanged(HmiPage value)
    {
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsConfigPage));
        OnPropertyChanged(nameof(IsTrendsPage));
    }

    [RelayCommand]
    private void ShowHome() => CurrentPage = HmiPage.Home;

    [RelayCommand]
    private void ShowConfig() => CurrentPage = HmiPage.Config;

    [RelayCommand]
    private void ShowTrends() => CurrentPage = HmiPage.Trends;

    // ---- Home overview card data ----

    public int TagCount => Tags.Count;

    public bool HasDisplay => DisplaySurface.HasDocument;

    public string DisplayTitle => HasDisplay ? DisplaySurface.DisplayName : "No display loaded";

    public void SetOwnerWindow(Window? owner) => ownerWindow_ = owner;

    [ObservableProperty]
    private string _baseUrl = "http://127.0.0.1:8080";

    [ObservableProperty]
    private string _displayStoreUrl = "http://127.0.0.1:8080";

    /// <summary>
    /// Extra bridges as lines: id|http://host:8080
    /// Primary BaseUrl is always included as bridge id "default" unless listed.
    /// </summary>
    [ObservableProperty]
    private string _bridgeListText = string.Empty;

    [ObservableProperty]
    private string _bridgeSummary = string.Empty;

    [ObservableProperty]
    private string _connectionState = "Disconnected";

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private TagItemViewModel? _selectedTag;

    /// <summary>
    /// Source the tag browser is limited to. The "all sources" entry (or null) shows every source.
    /// </summary>
    [ObservableProperty]
    private SourceFilterOption? _selectedSourceFilter;

    /// <summary>
    /// Bridge the tag browser is limited to. The "all bridges" entry (or null) shows every bridge,
    /// and drives which sources the source selector lists.
    /// </summary>
    [ObservableProperty]
    private BridgeFilterOption? _selectedBridgeFilter;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private DisplayEntry? _selectedDisplay;

    public ObservableCollection<TagItemViewModel> Tags { get; } = new();

    public ObservableCollection<DisplayEntry> Displays { get; } = new();

    /// <summary>One editable line per bridge server (address, store, name, status).</summary>
    public ObservableCollection<BridgeRow> BridgeRows { get; } = new();

    /// <summary>
    /// Rows the tag browser shows: <see cref="Tags"/> narrowed by the bridge/source selectors and
    /// the filter text. Kept in step row by row by <see cref="RefreshFilteredTags"/>.
    /// </summary>
    public ObservableCollection<TagItemViewModel> FilteredTags { get; } = new();

    /// <summary>
    /// Snapshot of the tag metadata the trend-group picker lists. Every bridge/source is included
    /// (the picker has no selectors, so it must not inherit the tag browser's choice) and rows carry
    /// names only — no live values — so a multi-select is not disturbed by the value stream. Tags
    /// already in <paramref name="alreadyInGroup"/> are listed disabled with an "in group" marker.
    /// </summary>
    public IReadOnlyList<TagListRow> BuildPickerRows(ISet<TagBindingKey>? alreadyInGroup = null)
    {
        var rows = new List<TagListRow>();
        foreach (MultiBridgeTagEntry entry in connections_.Cache.Tags
            .OrderBy(t => t.Key.BridgeId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Key.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            TagItemViewModel tag = TagItemViewModel.FromEntry(entry);
            tag.InfluxConnected = IsBridgeInfluxConnected(tag.BridgeId);
            bool inGroup = alreadyInGroup?.Contains(entry.Key) == true;
            rows.Add(new TagListRow(
                entry.Key,
                tag.BridgeId,
                tag.SourceName,
                tag.DisplayName,
                tag.DaItemId,
                inGroup ? "in group" : tag.AvailabilityMarker,
                tag.CanTrend && !inGroup));
        }

        return rows;
    }

    /// <summary>Bridge selector entries: "all bridges" plus one per connected bridge.</summary>
    public ObservableCollection<BridgeFilterOption> BridgeFilters { get; } = new();

    /// <summary>Source selector entries: "all sources" plus one per source of the selected bridge.</summary>
    public ObservableCollection<SourceFilterOption> SourceFilters { get; } = new();

    partial void OnFilterChanged(string value) => RefreshFilteredTags();

    partial void OnSelectedSourceFilterChanged(SourceFilterOption? value) => RefreshFilteredTags();

    partial void OnSelectedBridgeFilterChanged(BridgeFilterOption? value)
    {
        // The source selector lists one bridge's sources, so picking a bridge re-scopes it.
        RebuildSourceFilters();
        RefreshFilteredTags();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        OpenFaceplateCommand.NotifyCanExecuteChanged();
        OpenTrendCommand.NotifyCanExecuteChanged();
        OpenGroupTrendCommand.NotifyCanExecuteChanged();
        NewTrendGroupCommand.NotifyCanExecuteChanged();
        AddTagsToTrendGroupCommand.NotifyCanExecuteChanged();
        RefreshDisplaysCommand.NotifyCanExecuteChanged();
        LoadSelectedDisplayCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(InfluxUnavailableHint));
    }

    partial void OnSelectedTagChanged(TagItemViewModel? value)
    {
        OpenFaceplateCommand.NotifyCanExecuteChanged();
        OpenTrendCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TrendDisabledHint));
    }

    /// <summary>
    /// Shown next to the tag browser when the selected tag has no InfluxDB history, so the
    /// operator understands why the Trend button is disabled.
    /// </summary>
    public string TrendDisabledHint => SelectedTag is { InfluxEnabled: false }
        ? "Trend disabled: this tag has no InfluxDB history (enable \"Influx log\" on the tag in the bridge dashboard)."
        : string.Empty;

    /// <summary>
    /// Global hint shown while no connected bridge is currently writing to InfluxDB.
    /// </summary>
    public string InfluxUnavailableHint => IsConnected && !IsInfluxAvailable
        ? "Trends disabled: no connected bridge is currently writing to InfluxDB."
        : string.Empty;

    /// <summary>True while at least one connected bridge is currently connected to InfluxDB.</summary>
    public bool IsInfluxAvailable
    {
        get
        {
            lock (bridgeInfluxConnected_)
            {
                return bridgeInfluxConnected_.Values.Any(v => v);
            }
        }
    }

    /// <summary>True when the given bridge's live InfluxDB writer is connected.</summary>
    public bool IsBridgeInfluxConnected(string bridgeId)
    {
        lock (bridgeInfluxConnected_)
        {
            return bridgeInfluxConnected_.TryGetValue(bridgeId, out bool connected) && connected;
        }
    }

    private void OnInfluxStatusChanged(string bridgeId, bool connected)
    {
        _ = PostToUiAsync(() =>
        {
            lock (bridgeInfluxConnected_)
            {
                bridgeInfluxConnected_[bridgeId] = connected;
            }

            foreach (TagItemViewModel tag in Tags)
            {
                tag.InfluxConnected = connected && string.Equals(tag.BridgeId, bridgeId, StringComparison.OrdinalIgnoreCase);
            }

            OnPropertyChanged(nameof(IsInfluxAvailable));
            OnPropertyChanged(nameof(InfluxUnavailableHint));
            OnPropertyChanged(nameof(TrendDisabledHint));
            OpenTrendCommand.NotifyCanExecuteChanged();
            OpenGroupTrendCommand.NotifyCanExecuteChanged();
            NewTrendGroupCommand.NotifyCanExecuteChanged();
            AddTagsToTrendGroupCommand.NotifyCanExecuteChanged();
            foreach (FaceplateViewModel faceplate in openFaceplates_)
            {
                faceplate.NotifyInfluxAvailabilityChanged();
            }

            StatusMessage = connected
                ? $"InfluxDB connected on {bridgeId} — trends available"
                : $"InfluxDB connection lost on {bridgeId} — trends disabled";
        });
    }

    [RelayCommand]
    private void AddBridge() => AddBridgeRow();

    [RelayCommand]
    private void RemoveBridge(BridgeRow? row)
    {
        if (row is not null)
        {
            BridgeRows.Remove(row);
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        connectCts_?.Cancel();
        connectCts_?.Dispose();
        connectCts_ = new CancellationTokenSource();
        CancellationToken ct = connectCts_.Token;

        try
        {
            ConnectionState = "Connecting";
            StatusMessage = string.Empty;

            HmiClientConfig config = BuildConfigFromRows(BridgeRows);
            if (config.Bridges.Count == 0)
            {
                ConnectionState = "Disconnected";
                StatusMessage = "Add at least one bridge address";
                return;
            }

            foreach (HmiBridgeEndpoint bridge in config.EnabledBridges())
            {
                string store = StoreUrlOf(bridge);
                if (!storeClients_.ContainsKey(store))
                {
                    DisplayStoreClient client = new();
                    client.SetBaseAddress(store);
                    storeClients_[store] = client;
                }
            }

            await connections_.ConnectAllAsync(config, ct).ConfigureAwait(true);
            SaveLocalConfig(config);
            RebuildTagsFromCache();
            RefreshTrendGroupRows();
            await RefreshDisplaysAsync().ConfigureAwait(true);

            IReadOnlyCollection<string> connected = connections_.ConnectedBridgeIds;
            List<BridgeRow> addressable = BridgeRows.Where(r => !string.IsNullOrWhiteSpace(r.Address)).ToList();
            for (int i = 0; i < addressable.Count && i < config.Bridges.Count; i++)
            {
                addressable[i].IsConnected = connected.Contains(config.Bridges[i].Id, StringComparer.OrdinalIgnoreCase);
            }

            IsConnected = true;
            ConnectionState = "Connected";
            BridgeSummary = string.Join(", ", connected);
            StatusMessage = $"Loaded {Tags.Count} tags from {connected.Count} bridge(s)";
            OnPropertyChanged(nameof(TagCount));
        }
        catch (OperationCanceledException)
        {
            await SafeDisconnectAsync().ConfigureAwait(true);
            ConnectionState = "Disconnected";
            StatusMessage = "Connect cancelled";
        }
        catch (Exception ex)
        {
            await SafeDisconnectAsync().ConfigureAwait(true);
            ConnectionState = "Disconnected";
            StatusMessage = ex.Message;
        }
    }

    private bool CanConnect() => !IsConnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task Disconnect()
    {
        connectCts_?.Cancel();
        await SafeDisconnectAsync().ConfigureAwait(true);
        ConnectionState = "Disconnected";
        StatusMessage = "Disconnected";
    }

    private bool CanDisconnect() => IsConnected;

    [RelayCommand(CanExecute = nameof(CanOpenFaceplate))]
    private void OpenFaceplate()
    {
        if (SelectedTag is null)
        {
            return;
        }

        OpenFaceplateFor(SelectedTag.BindingKey);
    }

    private bool CanOpenFaceplate() => IsConnected && SelectedTag is not null;

    /// <summary>Opens the single-tag trend for the selected tag.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTrend))]
    private void OpenTrend()
    {
        if (SelectedTag is null)
        {
            return;
        }

        OpenTrendFor(SelectedTag.BindingKey);
    }

    private bool CanOpenTrend() => IsConnected && SelectedTag is { CanTrend: true };

    /// <summary>Opens the tag picker used to compose a throwaway (unsaved) multi-tag group trend.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenGroupTrend))]
    private void OpenGroupTrend() =>
        ShowPicker(
            BuildPickerRows(),
            "Group trend",
            "Open trend",
            askForName: false,
            result => OpenTrendGroup(result.Keys));

    private bool CanOpenGroupTrend() => IsConnected && Tags.Count > 0 && IsInfluxAvailable;

    /// <summary>
    /// Opens a group trend window plotting the given tags on one chart. Tags whose bridge
    /// is not connected, or that have no InfluxDB history, are skipped and reported.
    /// </summary>
    public void OpenTrendGroup(IReadOnlyList<TagBindingKey> keys)
    {
        List<TrendPenViewModel> pens = BuildPens(keys, savedGroup: null, out int skippedNoHistory, out int skippedUnavailable);
        if (pens.Count == 0)
        {
            StatusMessage = "Group trend: no selected tags on a connected bridge";
            return;
        }

        ReportSkippedTags(pens.Count, keys.Count, skippedNoHistory, skippedUnavailable);
        ShowTrendWindow(new TrendGroupViewModel(pens), onClosed: null);
    }

    // ---- Saved trend groups (Trend groups page) ----

    /// <summary>Trend groups saved on this client, listed by the trend groups page.</summary>
    public ObservableCollection<TrendGroupItemViewModel> TrendGroups { get; } = new();

    [ObservableProperty]
    private TrendGroupItemViewModel? _selectedTrendGroup;

    /// <summary>Tag row selected in the group's tag list; drives "Remove tag".</summary>
    [ObservableProperty]
    private TagListRow? _selectedGroupTag;

    public bool HasTrendGroups => TrendGroups.Count > 0;

    public bool HasSelectedTrendGroup => SelectedTrendGroup is not null;

    partial void OnSelectedTrendGroupChanged(TrendGroupItemViewModel? value)
    {
        SelectedGroupTag = null;
        OnPropertyChanged(nameof(HasSelectedTrendGroup));
        AddTagsToTrendGroupCommand.NotifyCanExecuteChanged();
        RemoveSelectedGroupTagCommand.NotifyCanExecuteChanged();
        DeleteTrendGroupCommand.NotifyCanExecuteChanged();
        OpenTrendGroupChartCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedGroupTagChanged(TagListRow? value) =>
        RemoveSelectedGroupTagCommand.NotifyCanExecuteChanged();

    /// <summary>Creates a named group from a tag selection and saves it.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateTrendGroup))]
    private void NewTrendGroup() =>
        ShowPicker(
            BuildPickerRows(),
            "New trend group",
            "Create group",
            askForName: true,
            result =>
            {
                var group = new TrendGroupItemViewModel(new TrendGroupDefinition
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = result.Name ?? string.Empty
                });
                foreach (TagBindingKey key in result.Keys)
                {
                    group.AddTag(key);
                }

                TrendGroups.Add(group);
                RefreshTrendGroupRows();
                SelectedTrendGroup = group;
                SaveTrendGroups();
                OnPropertyChanged(nameof(HasTrendGroups));
                StatusMessage = $"Trend group '{group.Name}' created with {group.Tags.Count} tag(s)";
            });

    private bool CanCreateTrendGroup() => IsConnected && Tags.Count > 0 && IsInfluxAvailable;

    /// <summary>Adds tags to the selected group; tags already in it are listed but not selectable.</summary>
    [RelayCommand(CanExecute = nameof(CanAddTagsToTrendGroup))]
    private void AddTagsToTrendGroup()
    {
        if (SelectedTrendGroup is not { } group)
        {
            return;
        }

        var existing = new HashSet<TagBindingKey>(group.Definition.Keys(), TagBindingKeyComparer.Instance);
        ShowPicker(
            BuildPickerRows(existing),
            $"Add tags to {group.Name}",
            "Add tags",
            askForName: false,
            result =>
            {
                int added = 0;
                foreach (TagBindingKey key in result.Keys)
                {
                    if (group.AddTag(key))
                    {
                        added++;
                    }
                }

                RefreshTrendGroupRows();
                SaveTrendGroups();
                StatusMessage = $"Added {added} tag(s) to '{group.Name}'";
            });
    }

    private bool CanAddTagsToTrendGroup() =>
        HasSelectedTrendGroup && IsConnected && Tags.Count > 0 && IsInfluxAvailable;

    /// <summary>Drops the selected tag from the selected group.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelectedGroupTag))]
    private void RemoveSelectedGroupTag()
    {
        if (SelectedTrendGroup is not { } group || SelectedGroupTag is not { } row)
        {
            return;
        }

        if (group.RemoveTag(row.Key))
        {
            RefreshTrendGroupRows();
            SaveTrendGroups();
            StatusMessage = $"Removed {row.DisplayName} from '{group.Name}'";
        }
    }

    private bool CanRemoveSelectedGroupTag() => HasSelectedTrendGroup && SelectedGroupTag is not null;

    /// <summary>Deletes the selected group from the page and from disk.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteTrendGroup))]
    private void DeleteTrendGroup()
    {
        if (SelectedTrendGroup is not { } group)
        {
            return;
        }

        TrendGroups.Remove(group);
        SelectedTrendGroup = TrendGroups.FirstOrDefault();
        SaveTrendGroups();
        OnPropertyChanged(nameof(HasTrendGroups));
        StatusMessage = $"Deleted trend group '{group.Name}'";
    }

    private bool CanDeleteTrendGroup() => HasSelectedTrendGroup;

    /// <summary>Opens the selected group's chart; closing it saves the pen setup back to the group.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTrendGroupChart))]
    private void OpenTrendGroupChart()
    {
        if (SelectedTrendGroup is { } group)
        {
            OpenSavedTrendGroup(group);
        }
    }

    private bool CanOpenTrendGroupChart() => HasSelectedTrendGroup;

    /// <summary>Persists the group list; called by the page after renames.</summary>
    public void SaveTrendGroups()
    {
        try
        {
            TrendGroupStore.Save(trendGroupsPath_, TrendGroups.Select(group => group.Definition));
        }
        catch
        {
            // non-fatal
        }
    }

    private void OpenSavedTrendGroup(TrendGroupItemViewModel group)
    {
        List<TagBindingKey> keys = group.Definition.Keys().ToList();
        List<TrendPenViewModel> pens = BuildPens(keys, group.Definition, out int skippedNoHistory, out int skippedUnavailable);
        if (pens.Count == 0)
        {
            StatusMessage = $"Trend group '{group.Name}': no tags on a connected bridge";
            return;
        }

        ReportSkippedTags(pens.Count, keys.Count, skippedNoHistory, skippedUnavailable);

        var viewModel = new TrendGroupViewModel(pens, group.Name)
        {
            LayoutMode = TrendGroupLayouts.Normalize(group.Definition.LayoutMode),
            YAxisMode = TrendGroupAxisModes.Normalize(group.Definition.YAxisMode)
        };
        ShowTrendWindow(viewModel, onClosed: () =>
        {
            CaptureTrendGroupState(group.Definition, viewModel);
            SaveTrendGroups();
            RefreshTrendGroupRows();
        });
    }

    /// <summary>
    /// Builds one pen per tag: metadata from the tag cache, plus the saved per-pen display state
    /// when the tag belongs to a saved group. Tags on a disconnected bridge, tags the bridge no
    /// longer has, and tags without InfluxDB history are skipped (and counted).
    /// </summary>
    private List<TrendPenViewModel> BuildPens(
        IReadOnlyList<TagBindingKey> keys,
        TrendGroupDefinition? savedGroup,
        out int skippedNoHistory,
        out int skippedUnavailable)
    {
        var pens = new List<TrendPenViewModel>();
        skippedNoHistory = 0;
        skippedUnavailable = 0;
        foreach (TagBindingKey key in keys)
        {
            if (!connections_.TryGetSession(key.BridgeId, out BridgeConnectionManager.BridgeSession? session)
                || session is null)
            {
                skippedUnavailable++;
                continue;
            }

            MultiBridgeTagEntry? entry = LookupTagEntry(key);
            if (entry is null)
            {
                skippedUnavailable++;
                continue;
            }

            if (!entry.InfluxEnabled || !IsBridgeInfluxConnected(key.BridgeId))
            {
                skippedNoHistory++;
                continue;
            }

            TrendGroupPenDefinition? saved = savedGroup?.Pens.FirstOrDefault(pen => pen.Key.EqualsIgnoreCase(key));
            var pen = new TrendPenViewModel(
                key,
                session.Api,
                displayName: entry.DisplayName,
                description: entry.Description,
                dataType: entry.DataType,
                unit: entry.Unit,
                rangeMin: entry.RangeMin,
                rangeMax: entry.RangeMax,
                trendStyle: entry.TrendStyle,
                color: string.IsNullOrWhiteSpace(saved?.Color) ? null : saved!.Color);
            if (saved is not null)
            {
                TrendGroupPenState.Apply(pen, saved);
            }

            pens.Add(pen);
        }

        return pens;
    }

    /// <summary>Copies what the operator changed in the chart window back onto the saved group.</summary>
    private static void CaptureTrendGroupState(TrendGroupDefinition definition, TrendGroupViewModel viewModel)
    {
        definition.LayoutMode = viewModel.LayoutMode;
        definition.YAxisMode = viewModel.YAxisMode;
        foreach (TrendPenViewModel pen in viewModel.Pens)
        {
            TrendGroupPenDefinition? saved = definition.Pens.FirstOrDefault(p => p.Key.EqualsIgnoreCase(pen.Key));
            if (saved is not null)
            {
                TrendGroupPenState.Capture(pen, saved);
            }
        }
    }

    private void ShowTrendWindow(TrendGroupViewModel viewModel, Action? onClosed)
    {
        var window = new TrendWindow(viewModel);
        if (onClosed is not null)
        {
            window.Closed += (_, _) => onClosed();
        }

        if (ownerWindow_ is { } owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    private void ShowPicker(
        IReadOnlyList<TagListRow> rows,
        string title,
        string confirmText,
        bool askForName,
        Action<TagPickerResult> onConfirmed) =>
        TrendGroupPickerWindow.ShowFor(
            ownerWindow_,
            new TagPickerViewModel(rows, title, confirmText, askForName),
            onConfirmed);

    private void ReportSkippedTags(int shown, int total, int skippedNoHistory, int skippedUnavailable)
    {
        if (shown >= total)
        {
            return;
        }

        var reasons = new List<string>();
        if (skippedNoHistory > 0)
        {
            reasons.Add($"{skippedNoHistory} without InfluxDB history");
        }

        if (skippedUnavailable > 0)
        {
            reasons.Add($"{skippedUnavailable} not on a connected bridge");
        }

        StatusMessage = $"Group trend opened with {shown} of {total} tags ({string.Join(", ", reasons)})";
    }

    private void LoadTrendGroups()
    {
        TrendGroups.Clear();
        foreach (TrendGroupDefinition definition in TrendGroupStore.Load(trendGroupsPath_))
        {
            TrendGroups.Add(new TrendGroupItemViewModel(definition));
        }

        RefreshTrendGroupRows();
        SelectedTrendGroup = TrendGroups.FirstOrDefault();
        OnPropertyChanged(nameof(HasTrendGroups));
    }

    /// <summary>
    /// Rebuilds every group's tag rows from the tag cache. Deliberately not called from
    /// <see cref="RebuildTagsFromCache"/>: that runs on every value batch, and the page must not
    /// churn with the value stream.
    /// </summary>
    private void RefreshTrendGroupRows()
    {
        foreach (TrendGroupItemViewModel group in TrendGroups)
        {
            group.RefreshRows(LookupTagEntry);
        }

        SelectedGroupTag = null;
    }

    private MultiBridgeTagEntry? LookupTagEntry(TagBindingKey key) =>
        connections_.Cache.TryGet(key, out MultiBridgeTagEntry? entry) ? entry : null;

    [RelayCommand(CanExecute = nameof(CanRefreshDisplays))]
    private async Task RefreshDisplaysAsync()
    {
        Displays.Clear();
        List<string> errors = new();
        foreach ((string store, DisplayStoreClient client) in storeClients_)
        {
            try
            {
                DisplayListResponse list = await client.ListAsync(CancellationToken.None).ConfigureAwait(true);
                string label = BridgeRows.FirstOrDefault(r => r.StoreUrl == store)?.Name ?? store;
                foreach (DisplayListItemDto item in list.Items)
                {
                    Displays.Add(new DisplayEntry(store, label, item));
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{store}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            StatusMessage = "Display list: " + string.Join(" | ", errors);
        }
    }

    private bool CanRefreshDisplays() => IsConnected;

    [RelayCommand(CanExecute = nameof(CanLoadSelectedDisplay))]
    private async Task LoadSelectedDisplayAsync()
    {
        if (SelectedDisplay is null)
        {
            StatusMessage = "Select a display first";
            return;
        }

        try
        {
            if (!storeClients_.TryGetValue(SelectedDisplay.StoreUrl, out DisplayStoreClient? client) || client is null)
            {
                StatusMessage = "Store not connected: " + SelectedDisplay.StoreUrl;
                return;
            }

            DisplayDocumentDto? doc = await client.GetAsync(SelectedDisplay.Item.Id, CancellationToken.None)
                .ConfigureAwait(true);
            if (doc is null)
            {
                StatusMessage = "Display not found: " + SelectedDisplay.Item.Id;
                DisplaySurface.Clear();
                return;
            }

            DisplaySurface.Load(doc);
            StatusMessage = string.IsNullOrWhiteSpace(DisplaySurface.StatusMessage)
                ? $"Loaded display {doc.Name} ({doc.Widgets.Count} widgets)"
                : DisplaySurface.StatusMessage;
            CurrentPage = HmiPage.Home;
        }
        catch (Exception ex)
        {
            StatusMessage = "Load display: " + ex.Message;
        }
    }

    private bool CanLoadSelectedDisplay() => IsConnected && SelectedDisplay is not null;

    partial void OnSelectedDisplayChanged(DisplayEntry? value)
    {
        LoadSelectedDisplayCommand.NotifyCanExecuteChanged();
    }

    private async Task<(bool Ok, string? Error)> WriteForBindingAsync(TagBindingKey key, object? value)
    {
        if (!connections_.TryGetSession(key.BridgeId, out BridgeConnectionManager.BridgeSession? session)
            || session is null)
        {
            return (false, "Bridge not connected: " + key.BridgeId);
        }

        try
        {
            HmiWriteResponse response = await session.Api.WriteAsync(
                new HmiWriteRequest
                {
                    SourceId = key.SourceId,
                    ItemId = key.DaItemId,
                    Value = value
                },
                CancellationToken.None).ConfigureAwait(true);
            return (response.Ok, response.Error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void OpenFaceplateFor(TagBindingKey key)
    {
        popups_.OpenOrFocus(
            key,
            trend: false,
            factory: () =>
            {
                if (!connections_.TryGetSession(key.BridgeId, out BridgeConnectionManager.BridgeSession? session)
                    || session is null)
                {
                    throw new InvalidOperationException("Bridge not connected: " + key.BridgeId);
                }

                FaceplateViewModel vm = new(
                    key,
                    session.Api,
                    connections_.Cache,
                    openTrend: OpenTrendFor,
                    isInfluxAvailable: () => IsBridgeInfluxConnected(key.BridgeId));
                openFaceplates_.Add(vm);
                FaceplateWindow window = new(vm);
                window.Closed += (_, _) => openFaceplates_.Remove(vm);
                return window;
            },
            owner: ownerWindow_);
    }

    public void OpenTrendFor(TagBindingKey key)
    {
        if (!IsBridgeInfluxConnected(key.BridgeId))
        {
            StatusMessage = "Trend unavailable: the bridge is not connected to InfluxDB.";
            return;
        }

        popups_.OpenOrFocus(
            key,
            trend: true,
            factory: () =>
            {
                if (!connections_.TryGetSession(key.BridgeId, out BridgeConnectionManager.BridgeSession? session)
                    || session is null)
                {
                    throw new InvalidOperationException("Bridge not connected: " + key.BridgeId);
                }

                MultiBridgeTagEntry? tagEntry = connections_.Cache.TryGet(key, out MultiBridgeTagEntry? cached) ? cached : null;
                TrendViewModel vm = new(
                    key,
                    session.Api,
                    dataType: tagEntry?.DataType,
                    unit: tagEntry?.Unit,
                    rangeMin: tagEntry?.RangeMin,
                    rangeMax: tagEntry?.RangeMax,
                    trendStyle: tagEntry?.TrendStyle,
                    displayName: tagEntry?.DisplayName,
                    description: tagEntry?.Description);
                return new TrendWindow(vm);
            },
            owner: ownerWindow_);
    }

    private void OnCacheChanged()
    {
        _ = PostToUiAsync(() =>
        {
            RebuildTagsFromCache();
            DisplaySurface.RefreshLiveValues();
            foreach (FaceplateViewModel faceplate in openFaceplates_.ToArray())
            {
                faceplate.RefreshFromCache();
            }
        });
    }

    private Task OnMappingsChangedAsync(string bridgeId, HmiMappingsChanged msg)
    {
        return PostToUiAsync(async () =>
        {
            try
            {
                await connections_.RefreshBridgeSnapshotAsync(bridgeId, CancellationToken.None).ConfigureAwait(true);
                // Tags can appear/disappear with the mappings, so the saved groups' rows follow.
                RefreshTrendGroupRows();
                StatusMessage = $"Mappings changed on {bridgeId} (v{msg.Version})";
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
        });
    }

    /// <summary>
    /// Applies the tag cache to the browser. Live values arrive every ~100 ms, so a batch that
    /// leaves the tag set alone refreshes the existing rows in place: replacing the rows, or
    /// re-setting the bound list, makes the ListBox rebuild every container it holds, which drops
    /// the hover and the selection the operator is on. The collections are rebuilt only when the
    /// tag set itself changed (connect, disconnect, mapping edit).
    /// </summary>
    private void RebuildTagsFromCache()
    {
        TagBindingKey? selectedKey = SelectedTag?.BindingKey;
        MultiBridgeTagEntry[] entries = connections_.Cache.Tags
            .OrderBy(t => t.Key.BridgeId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Key.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        bool sameTags = HasSameTags(tagEntries_, entries);
        tagEntries_ = entries;

        if (sameTags)
        {
            foreach (MultiBridgeTagEntry entry in entries)
            {
                if (tagIndex_.TryGetValue(entry.Key, out TagItemViewModel? item))
                {
                    item.InfluxConnected = IsBridgeInfluxConnected(item.BridgeId);
                    item.Apply(entry);
                }
            }
        }
        else
        {
            Tags.Clear();
            FilteredTags.Clear();
            tagIndex_.Clear();
            foreach (MultiBridgeTagEntry entry in entries)
            {
                TagItemViewModel item = TagItemViewModel.FromEntry(entry);
                item.InfluxConnected = IsBridgeInfluxConnected(item.BridgeId);
                tagIndex_[entry.Key] = item;
                Tags.Add(item);
            }
        }

        RebuildBridgeFilters();
        RebuildSourceFilters();

        SelectedTag = selectedKey is { } key && tagIndex_.TryGetValue(key, out TagItemViewModel? still)
            ? still
            : null;
        RefreshFilteredTags();
        OnPropertyChanged(nameof(TagCount));
    }

    /// <summary>True when the cache still holds the same tags, in the same order.</summary>
    private static bool HasSameTags(MultiBridgeTagEntry[] previous, MultiBridgeTagEntry[] current)
    {
        if (previous.Length != current.Length)
        {
            return false;
        }

        for (int i = 0; i < current.Length; i++)
        {
            if (!previous[i].Key.EqualsIgnoreCase(current[i].Key))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Brings the browser's visible rows in line with the bridge/source selectors and the filter
    /// text. The text filter matches live values, so a value batch can change which rows belong in
    /// the list — the rows are synced one by one rather than the list being rebuilt.
    /// </summary>
    private void RefreshFilteredTags() =>
        FilteredRowSync.Apply(Tags, FilteredTags, IsTagVisible);

    private bool IsTagVisible(TagItemViewModel tag) =>
        MatchesSelectedFilters(tag) && MatchesTextFilter(tag);

    /// <summary>
    /// Rebuilds the bridge selector from the connected bridges, keeping the operator's current
    /// choice when that bridge is still connected. The entries are refreshed on every value batch,
    /// so the list is only refilled when they actually changed — a refill would make the ComboBox
    /// rebuild its items under the operator.
    /// </summary>
    private void RebuildBridgeFilters()
    {
        string? bridgeId = SelectedBridgeFilter?.BridgeId;
        IReadOnlyList<BridgeFilterOption> options = BridgeFilterOptions.Build(tagEntries_);

        if (!BridgeFilters.SequenceEqual(options))
        {
            BridgeFilters.Clear();
            foreach (BridgeFilterOption option in options)
            {
                BridgeFilters.Add(option);
            }
        }

        SelectedBridgeFilter = BridgeFilters.FirstOrDefault(option =>
            string.Equals(option.BridgeId, bridgeId, StringComparison.OrdinalIgnoreCase))
            ?? BridgeFilterOption.All;
    }

    /// <summary>
    /// Rebuilds the source selector for the selected bridge, keeping the operator's current
    /// choice when that source is still present. Like the bridge selector, the list is refreshed
    /// on every value batch and only refilled when its entries actually changed.
    /// </summary>
    private void RebuildSourceFilters()
    {
        string? bridgeId = SelectedSourceFilter?.BridgeId;
        string? sourceId = SelectedSourceFilter?.SourceId;
        IReadOnlyList<SourceFilterOption> options =
            SourceFilterOptions.Build(tagEntries_, SelectedBridgeFilter?.BridgeId);

        if (!SourceFilters.SequenceEqual(options))
        {
            SourceFilters.Clear();
            foreach (SourceFilterOption option in options)
            {
                SourceFilters.Add(option);
            }
        }

        SelectedSourceFilter = SourceFilters.FirstOrDefault(option =>
            string.Equals(option.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(option.BridgeId, bridgeId, StringComparison.OrdinalIgnoreCase))
            ?? SourceFilterOption.All;
    }

    private async Task SafeDisconnectAsync()
    {
        try
        {
            await connections_.DisconnectAllAsync().ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }

        IsConnected = false;
        Tags.Clear();
        FilteredTags.Clear();
        tagIndex_.Clear();
        tagEntries_ = Array.Empty<MultiBridgeTagEntry>();
        BridgeFilters.Clear();
        BridgeFilters.Add(BridgeFilterOption.All);
        SelectedBridgeFilter = BridgeFilterOption.All;
        SourceFilters.Clear();
        SourceFilters.Add(SourceFilterOption.All);
        SelectedSourceFilter = SourceFilterOption.All;
        lock (bridgeInfluxConnected_)
        {
            bridgeInfluxConnected_.Clear();
        }

        OnPropertyChanged(nameof(IsInfluxAvailable));
        OnPropertyChanged(nameof(InfluxUnavailableHint));
        Displays.Clear();
        SelectedTag = null;
        SelectedDisplay = null;
        DisplaySurface.Clear();
        foreach (BridgeRow row in BridgeRows)
        {
            row.IsConnected = false;
        }

        foreach (DisplayStoreClient client in storeClients_.Values)
        {
            client.Dispose();
        }

        storeClients_.Clear();
        OnPropertyChanged(nameof(TagCount));
    }

    private void LoadLocalConfig()
    {
        try
        {
            HmiClientConfig config = HmiClientConfig.LoadOrDefault(configPath_, BaseUrl);
            for (int i = 0; i < config.Bridges.Count; i++)
            {
                HmiBridgeEndpoint bridge = config.Bridges[i];
                // Old configs stored the store once, globally — migrate it into the first row.
                string store = i == 0 && string.IsNullOrWhiteSpace(bridge.DisplayStoreUrl)
                    ? config.DisplayStoreUrl
                    : bridge.DisplayStoreUrl;
                AddBridgeRow(bridge.Id, bridge.BaseUrl, store == bridge.BaseUrl ? string.Empty : store);
            }

            if (BridgeRows.Count == 0)
            {
                AddBridgeRow("default", config.DisplayStoreUrl, string.Empty);
            }

            RefreshPrimaryAddress();
        }
        catch
        {
            AddBridgeRow("default", "http://127.0.0.1:8080", string.Empty);
        }
    }

    public static HmiClientConfig BuildConfigFromRows(IEnumerable<BridgeRow> rows)
    {
        var config = new HmiClientConfig();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int extra = 1;
        foreach (BridgeRow row in rows)
        {
            string address = row.Address.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            string id = string.IsNullOrWhiteSpace(row.Name)
                ? (config.Bridges.Count == 0 ? "default" : "bridge" + (++extra))
                : row.Name.Trim();
            while (!usedIds.Add(id))
            {
                id += "-2";
            }

            config.Bridges.Add(new HmiBridgeEndpoint
            {
                Id = id,
                BaseUrl = address,
                DisplayStoreUrl = row.StoreUrl == address ? string.Empty : row.DisplayStore.Trim().TrimEnd('/'),
                Enabled = true
            });
        }

        if (config.Bridges.Count > 0)
        {
            HmiBridgeEndpoint first = config.Bridges[0];
            config.DisplayStoreUrl = StoreUrlOf(first);
        }

        return config;
    }

    private static string StoreUrlOf(HmiBridgeEndpoint bridge)
        => string.IsNullOrWhiteSpace(bridge.DisplayStoreUrl) ? bridge.BaseUrl : bridge.DisplayStoreUrl;

    private void SaveLocalConfig(HmiClientConfig config)
    {
        try
        {
            config.Save(configPath_);
        }
        catch
        {
            // non-fatal
        }
    }

    private bool MatchesSelectedFilters(TagItemViewModel tag) =>
        (SelectedBridgeFilter is null || SelectedBridgeFilter.Matches(tag.BridgeId))
        && (SelectedSourceFilter is null || SelectedSourceFilter.Matches(tag.BindingKey));

    private bool MatchesTextFilter(TagItemViewModel tag) =>
        string.IsNullOrWhiteSpace(Filter) || MatchesFilter(tag);

    private bool MatchesFilter(TagItemViewModel tag)
    {
        string f = Filter.Trim();
        return tag.BridgeId.Contains(f, StringComparison.OrdinalIgnoreCase)
            || tag.SourceId.Contains(f, StringComparison.OrdinalIgnoreCase)
            || tag.SourceName.Contains(f, StringComparison.OrdinalIgnoreCase)
            || tag.SourceType.Contains(f, StringComparison.OrdinalIgnoreCase)
            || tag.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase)
            || tag.DaItemId.Contains(f, StringComparison.OrdinalIgnoreCase)
            || tag.ValueText.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private static Task PostToUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private static Task PostToUiAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }

        var tcs = new TaskCompletionSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action().ConfigureAwait(true);
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public async ValueTask DisposeAsync()
    {
        connectCts_?.Cancel();
        connectCts_?.Dispose();
        connections_.CacheChanged -= OnCacheChanged;
        connections_.MappingsChanged -= OnMappingsChangedAsync;
        connections_.InfluxStatusChanged -= OnInfluxStatusChanged;
        foreach (DisplayStoreClient client in storeClients_.Values)
        {
            client.Dispose();
        }

        if (ownsServices_)
        {
            await connections_.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>One editable bridge line in the Config page: address, store, name, status.</summary>
public sealed partial class BridgeRow : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _address = string.Empty;

    /// <summary>Display store override; empty = the bridge's own server hosts the store.</summary>
    [ObservableProperty]
    private string _displayStore = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    public string ScopeKind => IsLocalAddress(Address) ? "Local" : "External";

    partial void OnAddressChanged(string value) => OnPropertyChanged(nameof(ScopeKind));

    public string StoreUrl
        => string.IsNullOrWhiteSpace(DisplayStore) ? Address.Trim().TrimEnd('/') : DisplayStore.Trim().TrimEnd('/');

    public static bool IsLocalAddress(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return true;
        }

        return uri.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
    }
}

/// <summary>A display from one bridge's store, labeled for the merged picker.</summary>
public sealed record DisplayEntry(string StoreUrl, string BridgeLabel, DisplayListItemDto Item)
{
    public string Label => string.IsNullOrWhiteSpace(BridgeLabel)
        ? Item.Name
        : $"{Item.Name} ({BridgeLabel})";
}
