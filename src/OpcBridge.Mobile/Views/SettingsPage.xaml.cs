using Microsoft.Maui.ApplicationModel;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

public partial class SettingsPage : ContentPage
{
    private readonly AppState state_;

    public SettingsPage(AppState state)
    {
        InitializeComponent();
        state_ = state;
        hostEntry.Text = state_.BaseUrl;
        state_.Changed += OnStateChanged;
        aboutLabel.Text = "OpcBridge Logic " + AppInfo.Current.VersionString;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Repaint();
    }

    private void OnStateChanged()
    {
        MainThread.BeginInvokeOnMainThread(Repaint);
    }

    private void Repaint()
    {
        connectionLabel.Text = state_.ConnectionText + " · " + state_.BaseUrl;
        MobileSession? session = state_.Session;
        sessionLabel.Text = session is null
            ? "not signed in"
            : (session.Authenticated
                ? $"signed in as {session.DisplayName} ({session.Role})"
                : (session.AuthEnabled ? "the bridge requires sign-in" : "the bridge runs without sign-in"));
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        connectButton.IsEnabled = false;
        try
        {
            MobileResult<string> result = await state_.ConnectAsync(hostEntry.Text ?? string.Empty, CancellationToken.None);
            if (result.Ok)
            {
                hostEntry.Text = result.Value;
            }
        }
        finally
        {
            connectButton.IsEnabled = true;
            Repaint();
        }
    }

    private async void OnSignInClicked(object? sender, EventArgs e)
    {
        signInButton.IsEnabled = false;
        try
        {
            MobileResult<MobileSession> result = await state_.SignInAsync(
                userEntry.Text ?? string.Empty,
                passwordEntry.Text ?? string.Empty,
                CancellationToken.None);
            if (!result.Ok)
            {
                await DisplayAlert("Sign-in failed", result.Error ?? "the bridge rejected the sign-in", "OK");
            }
            else
            {
                passwordEntry.Text = string.Empty;
            }
        }
        finally
        {
            signInButton.IsEnabled = true;
            Repaint();
        }
    }
}
