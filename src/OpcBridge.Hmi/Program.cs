using System;
using Avalonia;
using Avalonia.WebView.Desktop;

namespace OpcBridge.Hmi;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        if (OperatingSystem.IsWindows())
        {
            // The Logic page's embedded web view uses the Windows WebView2 runtime. The GTK
            // backend is deliberately not referenced: it collides with Avalonia's own HarfBuzz.
            builder.UseWindowWebView();
        }

        return builder;
    }
}
