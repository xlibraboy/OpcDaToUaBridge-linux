using System.Text.Json;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;

namespace OpcBridge.Hmi.ViewModels;

public partial class FaceplateViewModel : ObservableObject, IAsyncDisposable
{
    private readonly BridgeApiClient api_;
    private readonly MultiBridgeTagCache cache_;
    private readonly Action<TagBindingKey> openTrend_;
    private readonly bool ownsApi_;
    private CancellationTokenSource? trendCts_;

    public FaceplateViewModel(
        TagBindingKey key,
        BridgeApiClient api,
        MultiBridgeTagCache cache,
        Action<TagBindingKey> openTrend,
        bool ownsApi = false)
    {
        Key = key;
        api_ = api;
        cache_ = cache;
        openTrend_ = openTrend;
        ownsApi_ = ownsApi;
        RefreshFromCache();
        _ = LoadSparklineAsync();
    }

    public TagBindingKey Key { get; }

    [ObservableProperty]
    private string _title = "Faceplate";

    [ObservableProperty]
    private string _bridgeId = string.Empty;

    [ObservableProperty]
    private string _sourceId = string.Empty;

    [ObservableProperty]
    private string _daItemId = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _dataType = string.Empty;

    [ObservableProperty]
    private string _valueText = string.Empty;

    [ObservableProperty]
    private string _qualityText = string.Empty;

    [ObservableProperty]
    private string _timestampText = string.Empty;

    [ObservableProperty]
    private bool _writeable;

    [ObservableProperty]
    private string _writeValue = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _trendStatus = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<double> _trendValues = Array.Empty<double>();

    public void RefreshFromCache()
    {
        BridgeId = Key.BridgeId;
        SourceId = Key.SourceId;
        DaItemId = Key.DaItemId;
        if (cache_.TryGet(Key, out MultiBridgeTagEntry? entry) && entry is not null)
        {
            DisplayName = entry.DisplayName;
            DataType = entry.DataType;
            Writeable = entry.Writeable;
            ValueText = FormatValue(entry.Value);
            QualityText = FormatQuality(entry.DaQuality, entry.IsGood);
            TimestampText = entry.TimestampUtc is null
                ? string.Empty
                : entry.TimestampUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
            if (string.IsNullOrWhiteSpace(WriteValue))
            {
                WriteValue = ValueText;
            }
        }
        else
        {
            DisplayName = Key.DaItemId;
            ValueText = "—";
            QualityText = "Unbound";
        }

        Title = string.IsNullOrWhiteSpace(DisplayName) ? Key.DaItemId : DisplayName;
        WriteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        if (!Writeable)
        {
            StatusMessage = "Tag is not writeable.";
            return;
        }

        object? value = ParseWriteValue(WriteValue, DataType);
        if (value is null && !string.Equals(DataType, "String", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Could not parse write value for " + DataType;
            return;
        }

        try
        {
            HmiWriteResponse response = await api_.WriteAsync(
                new HmiWriteRequest
                {
                    SourceId = SourceId,
                    ItemId = DaItemId,
                    Value = value ?? WriteValue
                },
                CancellationToken.None).ConfigureAwait(true);
            StatusMessage = response.Ok
                ? "Write OK"
                : ("Write failed: " + (response.Error ?? "unknown"));
        }
        catch (Exception ex)
        {
            StatusMessage = "Write error: " + ex.Message;
        }
    }

    private bool CanWrite() => Writeable;

    [RelayCommand]
    private void OpenTrend() => openTrend_(Key);

    private async Task LoadSparklineAsync()
    {
        trendCts_?.Cancel();
        trendCts_?.Dispose();
        trendCts_ = new CancellationTokenSource();
        CancellationToken ct = trendCts_.Token;
        try
        {
            DateTime to = DateTime.UtcNow;
            DateTime from = to.AddHours(-1);
            HmiTrendResponse response = await api_.GetTrendsAsync(SourceId, DaItemId, from, to, 200, ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            List<double> values = new();
            foreach (HmiTrendPoint point in response.Points ?? Array.Empty<HmiTrendPoint>())
            {
                if (TryToDouble(point.V, out double y))
                {
                    values.Add(y);
                }
            }

            TrendValues = values;
            TrendStatus = string.IsNullOrWhiteSpace(response.Error)
                ? (values.Count == 0 ? "No history" : $"{values.Count} points (1h)")
                : response.Error!;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            TrendStatus = "Trend error: " + ex.Message;
            TrendValues = Array.Empty<double>();
        }
    }

    private static object? ParseWriteValue(string text, string dataType)
    {
        string t = (text ?? string.Empty).Trim();
        string dt = dataType ?? string.Empty;
        if (string.Equals(dt, "Boolean", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dt, "Bool", StringComparison.OrdinalIgnoreCase))
        {
            if (bool.TryParse(t, out bool b)) return b;
            if (t == "1") return true;
            if (t == "0") return false;
            return null;
        }

        if (string.Equals(dt, "String", StringComparison.OrdinalIgnoreCase))
        {
            return t;
        }

        if (string.Equals(dt, "Int32", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dt, "Int16", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dt, "Int64", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dt, "Byte", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(t, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long l))
            {
                return l;
            }

            return null;
        }

        if (double.TryParse(t, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d))
        {
            return d;
        }

        return null;
    }

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    private static string FormatQuality(int? daQuality, bool? isGood)
    {
        if (isGood == true) return daQuality is null ? "Good" : $"Good ({daQuality})";
        if (isGood == false) return daQuality is null ? "Bad" : $"Bad ({daQuality})";
        return daQuality is null ? string.Empty : daQuality.Value.ToString();
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
            case JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out double jd):
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
        trendCts_?.Cancel();
        trendCts_?.Dispose();
        if (ownsApi_)
        {
            api_.Dispose();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
