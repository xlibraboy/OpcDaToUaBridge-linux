using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpcBridge.Hmi.Views;

/// <summary>
/// A small yes/no confirmation dialog for destructive steps (deleting a logic block, dropping a
/// network). Returns true only when the operator confirms.
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    public static async Task<bool> ShowFor(Window? owner, string title, string message, string confirmText)
    {
        if (owner is null)
        {
            // Never confirmed without a dialog — the page always has an owner in the app.
            return true;
        }

        var window = new ConfirmWindow { Title = title };
        window.MessageText.Text = message;
        window.ConfirmButton.Content = confirmText;
        return await window.ShowDialog<bool>(owner);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
