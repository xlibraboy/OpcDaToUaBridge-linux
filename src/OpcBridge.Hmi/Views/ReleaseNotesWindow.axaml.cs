using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpcBridge.Client;

namespace OpcBridge.Hmi.Views;

/// <summary>
/// The app's own release notes — its CHANGELOG.md, embedded at build time — opened from
/// Help ▸ Release notes. Shared by the runtime and the designer, each passing its own
/// assembly and resource name; markdown is shown as it is written, so no renderer is
/// involved.
/// </summary>
public partial class ReleaseNotesWindow : Window
{
    public ReleaseNotesWindow()
    {
        InitializeComponent();
    }

    public static void ShowFor(Window? owner, string productName, Assembly assembly, string resourceName)
    {
        var window = new ReleaseNotesWindow
        {
            Title = $"Release notes — {productName} {ReleaseNotes.InformationalVersion(assembly)}"
        };
        window.Notes.Text = ReleaseNotes.Load(assembly, resourceName);

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
