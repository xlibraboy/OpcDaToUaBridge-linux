using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;

namespace OpcBridge.Hmi.ViewModels;

public partial class TrendViewModel : ObservableObject, IAsyncDisposable
{
    private readonly BridgeApiClient api_;
    private readonly bool ownsApi_;
    private CancellationTokenSource? cts_;
    private readonly PeriodicTimer? refreshTimer_;
    private readonly Task? refreshLoop_;

    public TrendViewModel(TagBindingKey key, BridgeApiClient api, bool ownsApi = false)
    {
        Key = key;
        api_ = api;
        ownsApi_ = ownsApi;
        Title = $"{key.BridgeId} / {key.DaItemId}";
        BridgeId = key.BridgeId;
        SourceId = key.SourceId;
        DaItemId = key.DaItemId;
        _ = ReloadAsync();
        refreshTimer_ = new PeriodicTimer(TimeSpan.FromSeconds(30));
        refreshLoop_ = RefreshLoopAsync();
    }

    public TagBindingKey Key { get; }

    [ObservableProperty]
    private string _title = "Trend";

    [ObservableProperty]
    private string _bridgeId = string.Empty;

    [ObservableProperty]
    private string _sourceId = string.Empty;

    [ObservableProperty]
    private string _daItemId = string.Empty;

    [ObservableProperty]
    private string _rangeLabel = "1h";

    public bool IsRange1h => RangeLabel == "1h";
    public bool IsRange8h => RangeLabel == "8h";
    public bool IsRange24h => RangeLabel == "24h";

    partial void OnRangeLabelChanged(string value)
    {
        OnPropertyChanged(nameof(IsRange1h));
        OnPropertyChanged(nameof(IsRange8h));
        OnPropertyChanged(nameof(IsRange24h));
    }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private IReadOnlyList<double> _values = Array.Empty<double>();

    [RelayCommand]
    private async Task SetRangeAsync(string hoursText)
    {
        if (!int.TryParse(hoursText, out int hours) || hours <= 0)
        {
            hours = 1;
        }

        RangeLabel = hours + "h";
        await ReloadAsync(hours).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync().ConfigureAwait(true);

    private async Task RefreshLoopAsync()
    {
        if (refreshTimer_ is null)
        {
            return;
        }

        try
        {
            while (await refreshTimer_.WaitForNextTickAsync().ConfigureAwait(true))
            {
                await ReloadAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReloadAsync(int? hours = null)
    {
        int rangeHours = hours ?? ParseRangeHours(RangeLabel);
        cts_?.Cancel();
        cts_?.Dispose();
        cts_ = new CancellationTokenSource();
        CancellationToken ct = cts_.Token;
        IsLoading = true;
        try
        {
            DateTime to = DateTime.UtcNow;
            DateTime from = to.AddHours(-rangeHours);
            HmiTrendResponse response = await api_.GetTrendsAsync(SourceId, DaItemId, from, to, 1000, ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            List<double> points = new();
            foreach (HmiTrendPoint point in response.Points ?? Array.Empty<HmiTrendPoint>())
            {
                if (TryToDouble(point.V, out double y))
                {
                    points.Add(y);
                }
            }

            Values = points;
            StatusMessage = string.IsNullOrWhiteSpace(response.Error)
                ? (points.Count == 0 ? "No history" : $"{points.Count} points ({RangeLabel})")
                : response.Error!;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = "Trend error: " + ex.Message;
            Values = Array.Empty<double>();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static int ParseRangeHours(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return 1;
        }

        string digits = new string(label.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int h) && h > 0 ? h : 1;
    }

    private static bool TryToDouble(object? value, out double y)
    {
        switch (value)
        {
            case null:
                y = 0;
                return false;
            case double d:
                y = d;
                return true;
            case float f:
                y = f;
                return true;
            case int i:
                y = i;
                return true;
            case long l:
                y = l;
                return true;
            case System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number
                && je.TryGetDouble(out double jd):
                y = jd;
                return true;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double sd):
                y = sd;
                return true;
            default:
                y = 0;
                return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts_?.Cancel();
        cts_?.Dispose();
        refreshTimer_?.Dispose();
        if (refreshLoop_ is not null)
        {
            try { await refreshLoop_.ConfigureAwait(false); } catch { }
        }

        if (ownsApi_)
        {
            api_.Dispose();
        }
    }
}
