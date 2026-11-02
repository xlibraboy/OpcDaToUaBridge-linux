namespace OpcBridge.Mobile.Views;

/// <summary>
/// The compact sync indicator: a coloured dot and the state word ("SYNCED", "SYNCING…",
/// "OFFLINE · 2 pending", "2 failed"). The word keeps the state readable without colour, and
/// the accessible description carries the last-sync/pending detail.
/// </summary>
public partial class SyncBadgeView : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text),
        typeof(string),
        typeof(SyncBadgeView),
        string.Empty,
        propertyChanged: OnChanged);

    public static readonly BindableProperty StateKeyProperty = BindableProperty.Create(
        nameof(StateKey),
        typeof(string),
        typeof(SyncBadgeView),
        "offline",
        propertyChanged: OnChanged);

    public static readonly BindableProperty DescriptionProperty = BindableProperty.Create(
        nameof(Description),
        typeof(string),
        typeof(SyncBadgeView),
        string.Empty,
        propertyChanged: OnChanged);

    public static readonly BindableProperty UppercaseProperty = BindableProperty.Create(
        nameof(Uppercase),
        typeof(bool),
        typeof(SyncBadgeView),
        true,
        propertyChanged: OnChanged);

    public static readonly BindableProperty HintProperty = BindableProperty.Create(
        nameof(Hint),
        typeof(string),
        typeof(SyncBadgeView),
        "Open Settings to see pending sync items",
        propertyChanged: OnChanged);

    public static readonly BindableProperty TextSizeProperty = BindableProperty.Create(
        nameof(TextSize),
        typeof(double),
        typeof(SyncBadgeView),
        9.0,
        propertyChanged: OnChanged);

    public SyncBadgeView()
    {
        InitializeComponent();
        Update();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string StateKey
    {
        get => (string)GetValue(StateKeyProperty);
        set => SetValue(StateKeyProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Sync words are shouted; the latency measurement keeps its lowercase unit ("12 ms").</summary>
    public bool Uppercase
    {
        get => (bool)GetValue(UppercaseProperty);
        set => SetValue(UppercaseProperty, value);
    }

    /// <summary>The accessible hint; empty for a badge that is not a Settings shortcut.</summary>
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>The design's mono reading size: 9 for sync, 9.5 for latency.</summary>
    public double TextSize
    {
        get => (double)GetValue(TextSizeProperty);
        set => SetValue(TextSizeProperty, value);
    }

    private static void OnChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((SyncBadgeView)bindable).Update();

    private void Update()
    {
        string text = Text ?? string.Empty;
        textLabel.Text = Uppercase ? text.ToUpperInvariant() : text;
        textLabel.FontSize = TextSize;
        Color color = ColorFor(StateKey);
        dotView.Color = color;
        textLabel.TextColor = color;
        SemanticProperties.SetDescription(this, Description);
        SemanticProperties.SetHint(this, Hint);
    }

    private static Color ColorFor(string key)
    {
        string resource = key switch
        {
            "synced" or "good" => "GoodColor",
            "syncing" or "pending" or "fair" => "WarnColor",
            "error" or "poor" => "BadColor",
            _ => "MutedColor"
        };

        if (Application.Current is { } application &&
            application.Resources.TryGetValue(resource, out object? value) &&
            value is Color color)
        {
            return color;
        }

        return key switch
        {
            "synced" or "good" => Color.FromArgb("#4CC38A"),
            "syncing" or "pending" or "fair" => Color.FromArgb("#E2A336"),
            "error" or "poor" => Color.FromArgb("#E5484D"),
            _ => Color.FromArgb("#93A19B")
        };
    }
}
