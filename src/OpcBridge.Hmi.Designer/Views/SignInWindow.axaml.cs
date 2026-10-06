using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OpcBridge.Hmi.Designer.Views;

/// <summary>
/// Sign-in dialog for a bridge with authentication enabled. The attempt runs through the
/// supplied callback so a rejected sign-in keeps the dialog open with the server's message.
/// </summary>
public partial class SignInWindow : Window
{
    private Func<string, string, Task<(bool Ok, string? Error)>>? signIn_;
    private bool busy_;

    public SignInWindow() => InitializeComponent();

    /// <summary>Shown while the bridge reports that authentication is enabled.</summary>
    public static Task ShowAsync(
        Window owner,
        string bridgeUrl,
        Func<string, string, Task<(bool Ok, string? Error)>> signIn)
    {
        var window = new SignInWindow { signIn_ = signIn };
        window.BridgeText.Text = $"Sign in to {bridgeUrl} to load its sources and their connection state.";
        return window.ShowDialog(owner);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UsernameBox.Focus();
    }

    private async void OnSignInClick(object? sender, RoutedEventArgs e) => await SignInAsync();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SignInAsync();
        }
    }

    private async Task SignInAsync()
    {
        if (busy_ || signIn_ is null)
        {
            return;
        }

        string username = UsernameBox.Text?.Trim() ?? string.Empty;
        string password = PasswordBox.Text ?? string.Empty;
        if (username.Length == 0)
        {
            ShowError("Enter your username.");
            UsernameBox.Focus();
            return;
        }

        busy_ = true;
        SignInButton.IsEnabled = false;
        ErrorText.IsVisible = false;
        try
        {
            (bool ok, string? error) = await signIn_(username, password);
            if (ok)
            {
                Close();
                return;
            }

            ShowError(error ?? "Sign-in failed.");
            PasswordBox.Text = string.Empty;
            PasswordBox.Focus();
        }
        finally
        {
            busy_ = false;
            SignInButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
