using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Microsoft.Maui.ApplicationModel;

namespace OpcBridge.Mobile;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (Microsoft.Maui.Controls.Application.Current is { } application)
        {
            application.RequestedThemeChanged += OnRequestedThemeChanged;
        }

        ApplySystemBars();
    }

    protected override void OnDestroy()
    {
        if (Microsoft.Maui.Controls.Application.Current is { } application)
        {
            application.RequestedThemeChanged -= OnRequestedThemeChanged;
        }

        base.OnDestroy();
    }

    // uiMode is in ConfigurationChanges, so a theme switch reconfigures this activity instead
    // of recreating it; this is the fallback for a phone-driven day/night change.
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplySystemBars();
    }

    // UserAppTheme changes are not guaranteed to raise a configuration change, so the bars
    // follow the app's own theme event instead of only the activity lifecycle.
    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(ApplySystemBars);

    private void ApplySystemBars()
    {
        if (Window is null)
        {
            return;
        }

        bool light = Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Light;
        Android.Graphics.Color bar = Android.Graphics.Color.ParseColor(light ? "#F2F6F3" : "#0C1210");
        Window.SetStatusBarColor(bar);
        Window.SetNavigationBarColor(bar);

        if (AndroidX.Core.View.WindowCompat.GetInsetsController(Window, Window.DecorView) is { } controller)
        {
            controller.AppearanceLightStatusBars = light;
            controller.AppearanceLightNavigationBars = light;
        }
    }
}
