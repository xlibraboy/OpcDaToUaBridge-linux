using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Services;

namespace OpcBridge.Hmi.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly BridgeApiClient _api;
    private readonly HmiHubClient _hub;
    private readonly HmiTagCache _cache = new();
    private readonly Dictionary<string, TagItemViewModel> _tagIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _ownsClients;
    private CancellationTokenSource? _connectCts;
    private int _mappingVersion;

    public MainViewModel()
        : this(new BridgeApiClient(), new HmiHubClient(), ownsClients: true)
    {
        // Restore the last known bridge URL when available (skips the discovery scan).
        string? saved = HmiSettings.LoadBaseUrl();
        if (!string.IsNullOrWhiteSpace(saved))
        {
            _baseUrl = saved;
        }

        // Auto-discover the bridge HTTP port (the bridge moves off 8080 when that port is in use).
        _ = AutoDiscoverAsync();
    }

    public MainViewModel(BridgeApiClient api, HmiHubClient hub, bool ownsClients = false)
    {
        _api = api;
        _hub = hub;
        _ownsClients = ownsClients;
        _hub.Reconnected += OnHubReconnectedAsync;
    }

    [ObservableProperty]
    private string _baseUrl = "http://127.0.0.1:8080";

    [ObservableProperty]
    private string _connectionState = "Disconnected";

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private TagItemViewModel? _selectedTag;

    [ObservableProperty]
    private string _writeValue = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _trendStatus = string.Empty;

    [ObservableProperty]
    private bool _trendLoading;

    [ObservableProperty]
    private IReadOnlyList<double> _trendValues = Array.Empty<double>();

    /// <summary>Port summary line, e.g. "HTTP 8081 · OPC UA 4841" (empty until connected).</summary>
    [ObservableProperty]
    private string _portsSummary = string.Empty;

    /// <summary>True when any bridge port was auto-assigned away from its default.</summary>
    [ObservableProperty]
    private bool _portsWarning;

    public ObservableCollection<TagItemViewModel> Tags { get; } = new();

    public IEnumerable<TagItemViewModel> FilteredTags =>
        string.IsNullOrWhiteSpace(Filter)
            ? Tags
            : Tags.Where(MatchesFilter);

    partial void OnFilterChanged(string value) => OnPropertyChanged(nameof(FilteredTags));

    partial void OnIsConnectedChanged(bool value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        WriteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTagChanged(TagItemViewModel? value)
    {
        WriteValue = value?.ValueText ?? string.Empty;
        WriteCommand.NotifyCanExecuteChanged();
        _ = LoadTrendsForSelectionAsync();
    }

    private async Task AutoDiscoverAsync()
    {
        try
        {
            string discovered = await BridgePortDiscovery
                .DiscoverBaseUrlAsync(BaseUrl, CancellationToken.None)
                .ConfigureAwait(true);
            if (!string.Equals(discovered, BaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                await PostToUiAsync(() =>
                {
                    BaseUrl = discovered;
                    StatusMessage = $"Bridge auto-discovered at {discovered}";
                }).ConfigureAwait(true);
            }

            if (!string.Equals(discovered, HmiSettings.LoadBaseUrl(), StringComparison.OrdinalIgnoreCase))
            {
                HmiSettings.SaveBaseUrl(discovered);
            }
        }
        catch
        {
            // discovery is best-effort; the user can still connect manually
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();
        CancellationToken ct = _connectCts.Token;

        try
        {
            ConnectionState = "Connecting";
            StatusMessage = string.Empty;
            _api.SetBaseAddress(BaseUrl);

            await RefreshSnapshotAsync(ct).ConfigureAwait(true);

            await _hub.ConnectAsync(
                BaseUrl,
                OnValuesAsync,
                OnMappingsChangedAsync,
                ct).ConfigureAwait(true);

            await RefreshPortsSummaryAsync(ct).ConfigureAwait(true);

            IsConnected = true;
            ConnectionState = "Connected";
            StatusMessage = $"Loaded {Tags.Count} tags (v{_mappingVersion})";
            _ = TrendRefreshLoopAsync(ct);
            if (SelectedTag is not null)
            {
                await LoadTrendsAsync(ct).ConfigureAwait(true);
            }
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
        _connectCts?.Cancel();
        await SafeDisconnectAsync().ConfigureAwait(true);
        ConnectionState = "Disconnected";
        StatusMessage = "Disconnected";
    }

    private bool CanDisconnect() => IsConnected;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        if (SelectedTag is null)
        {
            return;
        }

        try
        {
            object? parsed = ParseWriteValue(WriteValue, SelectedTag.DataType);
            HmiWriteResponse response = await _api.WriteAsync(
                new HmiWriteRequest
                {
                    SourceId = SelectedTag.SourceId,
                    ItemId = SelectedTag.ItemId,
                    Value = parsed
                },
                CancellationToken.None).ConfigureAwait(true);

            StatusMessage = response.Ok
                ? "Write OK"
                : (response.Error ?? "Write failed");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private bool CanWrite() => IsConnected && SelectedTag is { Writeable: true };

    public void ApplySnapshot(HmiTagsResponse response)
    {
        _mappingVersion = (int)response.Version;
        _cache.ReplaceAll(response.Tags);

        string? selectedKey = SelectedTag?.Key;
        Tags.Clear();
        _tagIndex.Clear();

        foreach (HmiTagDto dto in response.Tags)
        {
            TagItemViewModel item = TagItemViewModel.FromDto(dto);
            _tagIndex[item.Key] = item;
            Tags.Add(item);
        }

        SelectedTag = selectedKey is not null && _tagIndex.TryGetValue(selectedKey, out TagItemViewModel? stillThere)
            ? stillThere
            : null;

        OnPropertyChanged(nameof(FilteredTags));
    }

    public void ApplyDeltas(IEnumerable<HmiValueDelta> deltas)
    {
        HmiValueDelta[] batch = deltas as HmiValueDelta[] ?? deltas.ToArray();
        _cache.ApplyDeltas(batch);

        foreach (HmiValueDelta delta in batch)
        {
            string key = HmiTagCache.Key(delta.SourceId, delta.ItemId);
            if (_tagIndex.TryGetValue(key, out TagItemViewModel? item))
            {
                item.ApplyDelta(delta);
            }
        }
    }

    private async Task OnValuesAsync(HmiValueDelta[] batch)
    {
        await PostToUiAsync(() => ApplyDeltas(batch)).ConfigureAwait(false);
    }

    private async Task OnMappingsChangedAsync(HmiMappingsChanged msg)
    {
        await PostToUiAsync(async () =>
        {
            try
            {
                await RefreshSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
                StatusMessage = $"Mappings changed (v{msg.Version})";
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
        }).ConfigureAwait(false);
    }

    private async Task OnHubReconnectedAsync(string? _)
    {
        await PostToUiAsync(async () =>
        {
            try
            {
                await RefreshSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
                ConnectionState = "Connected";
                StatusMessage = "Reconnected; snapshot refreshed";
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
        }).ConfigureAwait(false);
    }

    private async Task RefreshSnapshotAsync(CancellationToken ct)
    {
        HmiTagsResponse response = await _api.GetTagsAsync(ct).ConfigureAwait(true);
        ApplySnapshot(response);
    }

    private async Task RefreshPortsSummaryAsync(CancellationToken ct)
    {
        HmiPortInfo? info = await _api.GetPortsAsync(ct).ConfigureAwait(true);
        if (info is null)
        {
            return;
        }

        var parts = new List<string>();
        if (info.HttpAutoAssigned)
        {
            parts.Add($"HTTP {info.HttpPort} (auto-assigned, default {info.HttpDefault} in use)");
        }
        else
        {
            parts.Add($"HTTP {info.HttpPort}");
        }

        if (info.UaAutoAssigned)
        {
            parts.Add($"OPC UA {info.UaPort} (auto-assigned, default {info.UaDefault} in use)");
        }
        else
        {
            parts.Add($"OPC UA {info.UaPort}");
        }

        await PostToUiAsync(() =>
        {
            PortsSummary = string.Join(" · ", parts);
            PortsWarning = info.HttpAutoAssigned || info.UaAutoAssigned;
        }).ConfigureAwait(true);
    }

    private async Task SafeDisconnectAsync()
    {
        IsConnected = false;
        await _hub.DisposeAsync().ConfigureAwait(true);
        Tags.Clear();
        _tagIndex.Clear();
        _cache.ReplaceAll(Array.Empty<HmiTagDto>());
        SelectedTag = null;
        TrendValues = Array.Empty<double>();
        TrendStatus = string.Empty;
        TrendLoading = false;
        PortsSummary = string.Empty;
        PortsWarning = false;
        OnPropertyChanged(nameof(FilteredTags));
    }

    private async Task LoadTrendsForSelectionAsync()
    {
        if (_connectCts is null || !IsConnected)
        {
            return;
        }

        await LoadTrendsAsync(_connectCts.Token).ConfigureAwait(true);
    }

    private async Task TrendRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(true))
            {
                if (SelectedTag is not null)
                {
                    await LoadTrendsAsync(ct).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadTrendsAsync(CancellationToken ct)
    {
        TagItemViewModel? tag = SelectedTag;
        if (!IsConnected || tag is null)
        {
            TrendValues = Array.Empty<double>();
            TrendStatus = string.Empty;
            return;
        }

        TrendLoading = true;
        try
        {
            HmiTrendResponse response = await _api.GetTrendsAsync(
                tag.SourceId,
                tag.ItemId,
                fromUtc: null,
                toUtc: null,
                maxPoints: 500,
                ct).ConfigureAwait(true);

            List<double> ys = new();
            foreach (HmiTrendPoint p in response.Points)
            {
                if (TryToDouble(p.V, out double y))
                {
                    ys.Add(y);
                }
            }

            TrendValues = ys;
            if (!string.IsNullOrWhiteSpace(response.Error))
            {
                TrendStatus = response.Error!;
            }
            else if (ys.Count == 0)
            {
                TrendStatus = "No history";
            }
            else
            {
                TrendStatus = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            TrendValues = Array.Empty<double>();
            TrendStatus = ex.Message;
        }
        finally
        {
            TrendLoading = false;
        }
    }

    private static bool TryToDouble(object? value, out double y)
    {
        y = 0;
        if (value is null)
        {
            return false;
        }

        if (value is double d)
        {
            y = d;
            return true;
        }

        if (value is float f)
        {
            y = f;
            return true;
        }

        if (value is int i)
        {
            y = i;
            return true;
        }

        if (value is long l)
        {
            y = l;
            return true;
        }

        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out d))
            {
                y = d;
                return true;
            }

            return false;
        }

        return double.TryParse(
            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out y);
    }

    private bool MatchesFilter(TagItemViewModel tag)
    {
        string f = Filter.Trim();
        return tag.SourceId.Contains(f, StringComparison.OrdinalIgnoreCase)
            || tag.ItemId.Contains(f, StringComparison.OrdinalIgnoreCase)
            || tag.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase)
            || tag.ValueText.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private static object? ParseWriteValue(string text, string dataType)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string t = dataType.Trim();
        if (t.Equals("Boolean", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Bool", StringComparison.OrdinalIgnoreCase))
        {
            return bool.Parse(text);
        }

        if (t.Equals("Int16", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Short", StringComparison.OrdinalIgnoreCase))
        {
            return short.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (t.Equals("Int32", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Integer", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Int", StringComparison.OrdinalIgnoreCase))
        {
            return int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (t.Equals("Int64", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Long", StringComparison.OrdinalIgnoreCase))
        {
            return long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (t.Equals("Float", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Single", StringComparison.OrdinalIgnoreCase))
        {
            return float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (t.Equals("Double", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Float64", StringComparison.OrdinalIgnoreCase))
        {
            return double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (t.Equals("String", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
        {
            return d;
        }

        return text;
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
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _hub.Reconnected -= OnHubReconnectedAsync;
        await _hub.DisposeAsync().ConfigureAwait(false);
        if (_ownsClients)
        {
            _api.Dispose();
        }
    }
}
