using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels;

public partial class TagItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _sourceId = string.Empty;

    [ObservableProperty]
    private string _itemId = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _dataType = "Double";

    [ObservableProperty]
    private string _valueText = string.Empty;

    [ObservableProperty]
    private string _qualityText = string.Empty;

    [ObservableProperty]
    private string _timestampText = string.Empty;

    [ObservableProperty]
    private string _rateText = string.Empty;

    [ObservableProperty]
    private bool _writeable;

    public string Key => HmiTagCache.Key(SourceId, ItemId);

    public static TagItemViewModel FromDto(HmiTagDto dto)
    {
        var vm = new TagItemViewModel();
        vm.Apply(dto);
        return vm;
    }

    public void Apply(HmiTagDto dto)
    {
        SourceId = dto.SourceId;
        ItemId = dto.ItemId;
        DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? dto.ItemId : dto.DisplayName;
        DataType = dto.DataType;
        Writeable = dto.Writeable;
        RateText = dto.UpdateRateMs > 0 ? $"{dto.UpdateRateMs} ms" : "—";
        ApplyValue(dto.Value, dto.TimestampUtc, dto.DaQuality, dto.IsGood);
    }

    public void ApplyDelta(HmiValueDelta delta)
    {
        ApplyValue(delta.Value, delta.TimestampUtc, delta.DaQuality, delta.IsGood);
    }

    private void ApplyValue(object? value, DateTime? timestampUtc, int? daQuality, bool? isGood)
    {
        ValueText = FormatValue(value);
        QualityText = FormatQuality(daQuality, isGood);
        TimestampText = timestampUtc is null
            ? string.Empty
            : timestampUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
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
        if (isGood == true)
        {
            return daQuality is null ? "Good" : $"Good ({daQuality})";
        }

        if (isGood == false)
        {
            return daQuality is null ? "Bad" : $"Bad ({daQuality})";
        }

        return daQuality is null ? string.Empty : daQuality.Value.ToString();
    }
}
