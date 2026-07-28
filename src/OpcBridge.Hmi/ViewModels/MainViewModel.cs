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

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly BridgeConnectionManager connections_;
    private readonly DisplayStoreClient displayStore_;
    private readonly PopupWindowService popups_;
    private readonly Dictionary<string, TagItemViewModel> tagIndex_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool ownsServices_;
    private CancellationTokenSource? connectCts_;
    private Window? ownerWindow_;
    private readonly List<FaceplateViewModel> openFaceplates_ = new();

    public MainViewModel()
        : this(new BridgeConnectionManager(), new DisplayStoreClient(), new PopupWindowService(), ownsServices: true)
    {
    }

    public MainViewModel(
        BridgeConnectionManager connections,
        DisplayStoreClient displayStore,
        PopupWindowService popups,
        bool ownsServices = false)
    {
        connections_ = connections;
        displayStore_ = displayStore;
        popups_ = popups;
        ownsServices_ = ownsServices;
        DisplaySurface = new DisplaySurfaceViewModel(
            connections_.Cache,
            OpenFaceplateFor,
            WriteForBindingAsync);
        connections_.CacheChanged += OnCacheChanged;
        connections_.MappingsChanged += OnMappingsChangedAsync;
    }

    public DisplaySurfaceViewModel DisplaySurface { get; }

    public void SetOwnerWindow(Window? owner) => ownerWindow_ = owner;

    [ObservableProperty]
    private string _baseUrl = "http://127.0.0.1:8080";

    [ObservableProperty]
    private string _displayStoreUrl = "http://127.0.0.1:8080";

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
    private DisplayListItemDto? _selectedDisplay;

    public ObservableCollection<TagItemViewModel> Tags { get; } = new();

    public ObservableCollection<DisplayListItemDto> Displays { get; } = new();

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

    partial void OnBaseUrlChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(DisplayStoreUrl) || DisplayStoreUrl == "http://127.0.0.1:8080")
        {
            DisplayStoreUrl = value;
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

            HmiClientConfig config = HmiClientConfig.CreateDefaultSingleBridge(BaseUrl);
            if (!string.IsNullOrWhiteSpace(DisplayStoreUrl))
            {
                config.DisplayStoreUrl = DisplayStoreUrl.Trim().TrimEnd('/');
            }

            // If BaseUrl differs from a multi-entry future config, single-bridge default still works.
            displayStore_.SetBaseAddress(config.DisplayStoreUrl);
            await connections_.ConnectAllAsync(config, ct).ConfigureAwait(true);
            RebuildTagsFromCache();
            await RefreshDisplaysAsync().ConfigureAwait(true);

            IsConnected = true;
            ConnectionState = "Connected";
            StatusMessage = $"Loaded {Tags.Count} tags from {connections_.ConnectedBridgeIds.Count} bridge(s)";
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
        try
        {
            DisplayListResponse list = await displayStore_.ListAsync(CancellationToken.None).ConfigureAwait(true);
            Displays.Clear();
            foreach (DisplayListItemDto item in list.Items)
            {
                Displays.Add(item);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Display list: " + ex.Message;
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
            DisplayDocumentDto? doc = await displayStore_.GetAsync(SelectedDisplay.Id, CancellationToken.None)
                .ConfigureAwait(true);
            if (doc is null)
            {
                StatusMessage = "Display not found: " + SelectedDisplay.Id;
                DisplaySurface.Clear();
                return;
            }

            DisplaySurface.Load(doc);
            StatusMessage = string.IsNullOrWhiteSpace(DisplaySurface.StatusMessage)
                ? $"Loaded display {doc.Name} ({doc.Widgets.Count} widgets)"
                : DisplaySurface.StatusMessage;
        }
        catch (Exception ex)
        {
            StatusMessage = "Load display: " + ex.Message;
        }
    }

    private bool CanLoadSelectedDisplay() => IsConnected && SelectedDisplay is not null;

    partial void OnSelectedDisplayChanged(DisplayListItemDto? value)
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
                    DaItemId = key.DaItemId,
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
        OnPropertyChanged(nameof(FilteredTags));
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
        if (ownsServices_)
        {
            await connections_.DisposeAsync().ConfigureAwait(false);
            displayStore_.Dispose();
        }
    }
}
