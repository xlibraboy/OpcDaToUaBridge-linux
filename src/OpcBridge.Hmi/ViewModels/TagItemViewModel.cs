using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels;

public partial class TagItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _bridgeId = "default";

    [ObservableProperty]
    private string _sourceId = string.Empty;

    [ObservableProperty]
    private string _daItemId = string.Empty;

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
    private bool _writeable;

    [ObservableProperty]
    private string _unit = string.Empty;

    public TagBindingKey BindingKey => TagBindingKey.Create(BridgeId, SourceId, DaItemId);

    public string Key => BindingKey.CacheKey;

    public static TagItemViewModel FromEntry(MultiBridgeTagEntry entry)
    {
        var vm = new TagItemViewModel();
        vm.Apply(entry);
        return vm;
    }

    public static TagItemViewModel FromDto(string bridgeId, HmiTagDto dto)
    {
        var vm = new TagItemViewModel();
        vm.Apply(bridgeId, dto);
        return vm;
    }

    // Back-compat for older single-bridge call sites/tests.
    public static TagItemViewModel FromDto(HmiTagDto dto) => FromDto("default", dto);

    public void Apply(MultiBridgeTagEntry entry)
    {
        BridgeId = entry.Key.BridgeId;
        SourceId = entry.Key.SourceId;
        DaItemId = entry.Key.DaItemId;
        DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Key.DaItemId : entry.DisplayName;
        DataType = entry.DataType;
        Writeable = entry.Writeable;
        Unit = entry.Unit ?? string.Empty;
        ApplyValue(entry.Value, entry.TimestampUtc, entry.DaQuality, entry.IsGood);
    }

    public void Apply(string bridgeId, HmiTagDto dto)
    {
        BridgeId = bridgeId;
        SourceId = dto.SourceId;
        DaItemId = dto.ItemId;
        DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? dto.ItemId : dto.DisplayName;
        DataType = dto.DataType;
        Writeable = dto.Writeable;
        Unit = dto.Unit ?? string.Empty;
        ApplyValue(dto.Value, dto.TimestampUtc, dto.DaQuality, dto.IsGood);
    }

    public void Apply(HmiTagDto dto) => Apply("default", dto);

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
            : timestampUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
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
