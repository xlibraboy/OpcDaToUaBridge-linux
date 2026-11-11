namespace OpcBridge.Mobile.Views;

public partial class BottomTabBar : ContentView
{
    public static readonly BindableProperty ActiveTabProperty = BindableProperty.Create(
        nameof(ActiveTab),
        typeof(string),
        typeof(BottomTabBar),
        "logic");

    public BottomTabBar()
    {
        InitializeComponent();
    }

    public string ActiveTab
    {
        get => (string)GetValue(ActiveTabProperty);
        set => SetValue(ActiveTabProperty, value);
    }

    private async void OnLogicTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync("//logic");

    private async void OnElementsTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync("//elements");

    private async void OnSettingsTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync("//settings");
}
