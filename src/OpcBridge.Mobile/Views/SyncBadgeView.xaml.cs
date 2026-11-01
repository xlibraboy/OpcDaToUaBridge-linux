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

    private static void OnChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((SyncBadgeView)bindable).Update();

    private void Update()
    {
        string text = (Text ?? string.Empty).ToUpperInvariant();
        textLabel.Text = text;
        Color color = ColorFor(StateKey);
        dotLabel.TextColor = color;
        textLabel.TextColor = color;
        SemanticProperties.SetDescription(this, Description);
        SemanticProperties.SetHint(this, "Open Settings to see pending sync items");
    }

    private static Color ColorFor(string key)
    {
        string resource = key switch
        {
            "synced" => "GoodColor",
            "syncing" => "WarnColor",
            "pending" => "WarnColor",
            "error" => "BadColor",
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
            "synced" => Color.FromArgb("#4CC38A"),
            "syncing" or "pending" => Color.FromArgb("#E2A336"),
            "error" => Color.FromArgb("#E5484D"),
            _ => Color.FromArgb("#9AA6A0")
        };
    }
}
