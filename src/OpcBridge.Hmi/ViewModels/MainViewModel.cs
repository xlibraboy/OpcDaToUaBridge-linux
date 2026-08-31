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
    Config
}

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly BridgeConnectionManager connections_;
    private readonly Dictionary<string, DisplayStoreClient> storeClients_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly PopupWindowService popups_;
    private readonly Dictionary<string, TagItemViewModel> tagIndex_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool ownsServices_;
    private CancellationTokenSource? connectCts_;
    private Window? ownerWindow_;
    private readonly List<FaceplateViewModel> openFaceplates_ = new();
    private readonly string configPath_;

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
        DisplaySurface = new DisplaySurfaceViewModel(
            connections_.Cache,
            OpenFaceplateFor,
            WriteForBindingAsync);
        connections_.CacheChanged += OnCacheChanged;
        connections_.MappingsChanged += OnMappingsChangedAsync;
        DisplaySurface.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DisplaySurfaceViewModel.HasDocument) or nameof(DisplaySurfaceViewModel.DisplayName))
            {
                OnPropertyChanged(nameof(HasDisplay));
                OnPropertyChanged(nameof(DisplayTitle));
            }
        };
        LoadLocalConfig();
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

    partial void OnCurrentPageChanged(HmiPage value)
    {
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsConfigPage));
    }

    [RelayCommand]
    private void ShowHome() => CurrentPage = HmiPage.Home;

    [RelayCommand]
    private void ShowConfig() => CurrentPage = HmiPage.Config;

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

    public IEnumerable<TagItemViewModel> FilteredTags =>
        string.IsNullOrWhiteSpace(Filter)
            ? Tags
            : Tags.Where(MatchesFilter);

    partial void OnFilterChanged(string value) => OnPropertyChanged(nameof(FilteredTags));

    partial void OnIsConnectedChanged(bool value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        OpenFaceplateCommand.NotifyCanExecuteChanged();
        RefreshDisplaysCommand.NotifyCanExecuteChanged();
        LoadSelectedDisplayCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTagChanged(TagItemViewModel? value)
    {
        OpenFaceplateCommand.NotifyCanExecuteChanged();
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
                    openTrend: OpenTrendFor);
                openFaceplates_.Add(vm);
                FaceplateWindow window = new(vm);
                window.Closed += (_, _) => openFaceplates_.Remove(vm);
                return window;
            },
            owner: ownerWindow_);
    }

    public void OpenTrendFor(TagBindingKey key)
    {
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

                TrendViewModel vm = new(key, session.Api);
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
                StatusMessage = $"Mappings changed on {bridgeId} (v{msg.Version})";
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
        });
    }

    private void RebuildTagsFromCache()
    {
        string? selectedKey = SelectedTag?.Key;
        Tags.Clear();
        tagIndex_.Clear();
        foreach (MultiBridgeTagEntry entry in connections_.Cache.Tags
                     .OrderBy(t => t.Key.BridgeId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            TagItemViewModel item = TagItemViewModel.FromEntry(entry);
            tagIndex_[item.Key] = item;
            Tags.Add(item);
        }

        SelectedTag = selectedKey is not null && tagIndex_.TryGetValue(selectedKey, out TagItemViewModel? still)
            ? still
            : null;
        OnPropertyChanged(nameof(FilteredTags));
        OnPropertyChanged(nameof(TagCount));
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
        tagIndex_.Clear();
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
        OnPropertyChanged(nameof(FilteredTags));
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

    private bool MatchesFilter(TagItemViewModel tag)
    {
        string f = Filter.Trim();
        return tag.BridgeId.Contains(f, StringComparison.OrdinalIgnoreCase)
            || tag.SourceId.Contains(f, StringComparison.OrdinalIgnoreCase)
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
