using Avalonia.Controls;
using Avalonia.Interactivity;
using OpcBridge.Client;

namespace OpcBridge.Hmi.Views;

/// <summary>
/// The Logic page's guide — Help/LogicGuide.md, embedded at build time — opened from the page's
/// Guide… button. Markdown is shown as it is written, like Help ▸ Release notes does.
/// </summary>
public partial class LogicGuideWindow : Window
{
    public LogicGuideWindow()
    {
        InitializeComponent();
    }

    public static void ShowFor(Window? owner)
    {
        var window = new LogicGuideWindow
        {
            Title = $"Logic guide — OpcBridge HMI {ReleaseNotes.InformationalVersion(typeof(LogicGuideWindow).Assembly)}"
        };
        window.Guide.Text = ReleaseNotes.Load(typeof(LogicGuideWindow).Assembly, "OpcBridge.Hmi.LogicGuide.md");

        if (owner is null)
        {
            window.Show();
        }
        else
        {
            window.ShowDialog(owner);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
