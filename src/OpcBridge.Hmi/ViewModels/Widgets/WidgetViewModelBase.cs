using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels.Widgets;

public partial class WidgetViewModelBase : ObservableObject
{
    private readonly MultiBridgeTagCache? cache_;
    private readonly Action<TagBindingKey>? openFaceplate_;

    protected WidgetViewModelBase(
        DisplayWidgetDto dto,
        MultiBridgeTagCache? cache = null,
        Action<TagBindingKey>? openFaceplate = null)
    {
        Id = dto.Id;
        Type = dto.Type;
        X = dto.X;
        Y = dto.Y;
        Width = dto.W <= 0 ? 80 : dto.W;
        Height = dto.H <= 0 ? 32 : dto.H;
        Z = dto.Z;
        Props = dto.Props ?? new Dictionary<string, System.Text.Json.JsonElement>();
        cache_ = cache;
        openFaceplate_ = openFaceplate;

        if (dto.Binding is not null
            && !string.IsNullOrWhiteSpace(dto.Binding.BridgeId)
            && !string.IsNullOrWhiteSpace(dto.Binding.DaItemId))
        {
            Binding = TagBindingKey.Create(dto.Binding.BridgeId, dto.Binding.SourceId, dto.Binding.DaItemId);
        }

        RefreshFromCache();
    }

    public string Id { get; }
    public string Type { get; }

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private double _width = 80;

    [ObservableProperty]
    private double _height = 32;

    [ObservableProperty]
    private int _z;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isDesignMode;

    public bool CanResize => IsSelected && IsDesignMode;

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(CanResize));

    partial void OnIsDesignModeChanged(bool value) => OnPropertyChanged(nameof(CanResize));

    public Dictionary<string, System.Text.Json.JsonElement> Props { get; }

    public TagBindingKey? Binding { get; private set; }

    /// <summary>Design-time rebinding of a widget (may be null to unbind).</summary>
    public void UpdateBinding(TagBindingKey? binding)
    {
        Binding = binding;
        RefreshFromCache();
    }

    [ObservableProperty]
    private string _valueText = "—";

    [ObservableProperty]
    private string _qualityText = string.Empty;

    [ObservableProperty]
    private bool? _isGood;

    [ObservableProperty]
    private bool _isUnbound;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Design-time edit of the primary caption (label/text prop). No-op by default.</summary>
    public virtual void SetText(string text)
    {
    }

    public virtual void RefreshFromCache()
    {
        if (Binding is null)
        {
            if (!string.Equals(Type, DisplayWidgetTypes.Label, StringComparison.OrdinalIgnoreCase))
            {
                IsUnbound = true;
                ValueText = "—";
                StatusText = "Unbound";
            }

            return;
        }

        if (cache_ is null || !cache_.TryGet(Binding.Value, out MultiBridgeTagEntry? entry) || entry is null)
        {
            IsUnbound = true;
            ValueText = "—";
            QualityText = "Bridge not configured";
            StatusText = "Bridge not configured";
            IsGood = null;
            return;
        }

        IsUnbound = false;
        ValueText = FormatValue(entry.Value);
        QualityText = FormatQuality(entry.DaQuality, entry.IsGood);
        IsGood = entry.IsGood;
        StatusText = string.Empty;
        OnLiveValue(entry);
    }

    protected virtual void OnLiveValue(MultiBridgeTagEntry entry)
    {
    }

    [RelayCommand]
    private void OpenFaceplate()
    {
        if (Binding is null || openFaceplate_ is null)
        {
            return;
        }

        openFaceplate_(Binding.Value);
    }

    protected static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    protected static string FormatQuality(int? daQuality, bool? isGood)
    {
        if (isGood == true) return daQuality is null ? "Good" : $"Good ({daQuality})";
        if (isGood == false) return daQuality is null ? "Bad" : $"Bad ({daQuality})";
        return daQuality is null ? string.Empty : daQuality.Value.ToString();
    }

    public static WidgetViewModelBase Create(
        DisplayWidgetDto dto,
        MultiBridgeTagCache cache,
        Action<TagBindingKey> openFaceplate,
        Func<TagBindingKey, object?, Task<(bool Ok, string? Error)>>? writeAsync = null)
    {
        string type = dto.Type ?? string.Empty;
        if (string.Equals(type, DisplayWidgetTypes.Label, StringComparison.OrdinalIgnoreCase))
        {
            return new LabelWidgetViewModel(dto);
        }

        if (string.Equals(type, DisplayWidgetTypes.Numeric, StringComparison.OrdinalIgnoreCase))
        {
            return new NumericWidgetViewModel(dto, cache, openFaceplate);
        }

        if (string.Equals(type, DisplayWidgetTypes.QualityLamp, StringComparison.OrdinalIgnoreCase))
        {
            return new QualityLampWidgetViewModel(dto, cache, openFaceplate);
        }

        if (string.Equals(type, DisplayWidgetTypes.BoolIndicator, StringComparison.OrdinalIgnoreCase))
        {
            return new BoolIndicatorWidgetViewModel(dto, cache, openFaceplate);
        }

        if (string.Equals(type, DisplayWidgetTypes.PushButton, StringComparison.OrdinalIgnoreCase))
        {
            return new PushButtonWidgetViewModel(dto, cache, openFaceplate, writeAsync);
        }

        return new UnsupportedWidgetViewModel(dto);
    }
}

public sealed class LabelWidgetViewModel : WidgetViewModelBase
{
    public LabelWidgetViewModel(DisplayWidgetDto dto)
        : base(dto)
    {
        Text = DisplayPropReader.GetString(Props, "text", dto.Id);
        FontSize = DisplayPropReader.GetDouble(Props, "fontSize", 14);
    }

    public string Text { get; private set; }
    public double FontSize { get; }

    public override void SetText(string text)
    {
        Text = string.IsNullOrWhiteSpace(text) ? Id : text;
        Props["text"] = System.Text.Json.JsonSerializer.SerializeToElement(Text);
        OnPropertyChanged(nameof(Text));
    }
}

public sealed partial class NumericWidgetViewModel : WidgetViewModelBase
{
    public NumericWidgetViewModel(
        DisplayWidgetDto dto,
        MultiBridgeTagCache cache,
        Action<TagBindingKey> openFaceplate)
        : base(dto, cache, openFaceplate)
    {
        Label = DisplayPropReader.GetString(Props, "label");
        Format = DisplayPropReader.GetString(Props, "format", "G");
        UnitSource = DisplayPropReader.GetString(Props, "unitSource", "manual");
        Unit = UnitSource == "server" ? null : DisplayPropReader.GetString(Props, "unit");
    }

    public string Label { get; private set; }
    public string Format { get; }

    /// <summary>"server" = unit from the tag cache (dashboard-configured); "manual" = static widget prop.</summary>
    public string UnitSource { get; }

    public string? Unit { get; private set; }

    public override void SetText(string text)
    {
        Label = text;
        Props["label"] = System.Text.Json.JsonSerializer.SerializeToElement(text);
        OnPropertyChanged(nameof(Label));
    }

    protected override void OnLiveValue(MultiBridgeTagEntry entry)
    {
        if (entry.Value is IFormattable f && !string.IsNullOrWhiteSpace(Format) && Format != "G")
        {
            try
            {
                ValueText = f.ToString(Format, System.Globalization.CultureInfo.InvariantCulture) ?? FormatValue(entry.Value);
            }
            catch
            {
                ValueText = FormatValue(entry.Value);
            }
        }
        else
        {
            ValueText = FormatValue(entry.Value);
        }

        // Resolve unit: server mode reads from the tag cache entry; manual mode uses the widget prop.
        string? effectiveUnit = UnitSource == "server" ? entry.Unit : Unit;

        if (!string.IsNullOrWhiteSpace(effectiveUnit) && !string.IsNullOrWhiteSpace(ValueText))
        {
            ValueText = ValueText + " " + effectiveUnit;
        }
    }
}

public sealed partial class QualityLampWidgetViewModel : WidgetViewModelBase
{
    public QualityLampWidgetViewModel(
        DisplayWidgetDto dto,
        MultiBridgeTagCache cache,
        Action<TagBindingKey> openFaceplate)
        : base(dto, cache, openFaceplate)
    {
        Label = DisplayPropReader.GetString(Props, "label");
    }

    public string Label { get; private set; }

    public override void SetText(string text)
    {
        Label = text;
        Props["label"] = System.Text.Json.JsonSerializer.SerializeToElement(text);
        OnPropertyChanged(nameof(Label));
    }
}

public sealed partial class BoolIndicatorWidgetViewModel : WidgetViewModelBase
{
    public BoolIndicatorWidgetViewModel(
        DisplayWidgetDto dto,
        MultiBridgeTagCache cache,
        Action<TagBindingKey> openFaceplate)
        : base(dto, cache, openFaceplate)
    {
        Label = DisplayPropReader.GetString(Props, "label");
        OnText = DisplayPropReader.GetString(Props, "onText", "ON");
        OffText = DisplayPropReader.GetString(Props, "offText", "OFF");
    }

    public string Label { get; private set; }
    public string OnText { get; }
    public string OffText { get; }

    public override void SetText(string text)
    {
        Label = text;
        Props["label"] = System.Text.Json.JsonSerializer.SerializeToElement(text);
        OnPropertyChanged(nameof(Label));
    }

    [ObservableProperty]
    private bool _isOn;

    protected override void OnLiveValue(MultiBridgeTagEntry entry)
    {
        IsOn = CoerceBool(entry.Value);
        ValueText = IsOn ? OnText : OffText;
    }

    private static bool CoerceBool(object? value) => value switch
    {
        bool b => b,
        byte by => by != 0,
        short s => s != 0,
        int i => i != 0,
        long l => l != 0,
        float f => Math.Abs(f) > double.Epsilon,
        double d => Math.Abs(d) > double.Epsilon,
        string s when bool.TryParse(s, out bool b) => b,
        string s when s == "1" => true,
        string s when s == "0" => false,
        _ => false
    };
}

public sealed partial class PushButtonWidgetViewModel : WidgetViewModelBase
{
    private readonly Func<TagBindingKey, object?, Task<(bool Ok, string? Error)>>? writeAsync_;

    public PushButtonWidgetViewModel(
        DisplayWidgetDto dto,
        MultiBridgeTagCache cache,
        Action<TagBindingKey> openFaceplate,
        Func<TagBindingKey, object?, Task<(bool Ok, string? Error)>>? writeAsync)
        : base(dto, cache, openFaceplate)
    {
        Text = DisplayPropReader.GetString(Props, "text", "Write");
        Confirm = DisplayPropReader.GetBool(Props, "confirm", false);
        WriteValue = DisplayPropReader.GetWriteValue(Props);
        writeAsync_ = writeAsync;
    }

    public string Text { get; private set; }
    public bool Confirm { get; }
    public object? WriteValue { get; }

    public override void SetText(string text)
    {
        Text = string.IsNullOrWhiteSpace(text) ? "Write" : text;
        Props["text"] = System.Text.Json.JsonSerializer.SerializeToElement(Text);
        OnPropertyChanged(nameof(Text));
    }

    [RelayCommand]
    private async Task PressAsync()
    {
        if (Binding is null || writeAsync_ is null)
        {
            StatusText = "Not bound";
            return;
        }

        (bool ok, string? error) = await writeAsync_(Binding.Value, WriteValue).ConfigureAwait(true);
        StatusText = ok ? "Write OK" : (error ?? "Write failed");
    }
}

public sealed class UnsupportedWidgetViewModel : WidgetViewModelBase
{
    public UnsupportedWidgetViewModel(DisplayWidgetDto dto)
        : base(dto)
    {
        StatusText = "Unsupported: " + dto.Type;
        ValueText = dto.Type;
    }
}
