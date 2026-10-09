using Microsoft.Maui.ApplicationModel;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using OpcBridge.Mobile.Views;

namespace OpcBridge.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddSingleton<IBridgeSettings, MauiBridgeSettings>();
        builder.Services.AddSingleton<MultiBridgeTagCache>();
        builder.Services.AddSingleton(services => new BridgeCoordinator(
            services.GetRequiredService<IBridgeSettings>(),
            services.GetRequiredService<MultiBridgeTagCache>(),
            action => MainThread.BeginInvokeOnMainThread(action)));
        builder.Services.AddTransient<LogicPage>();
        builder.Services.AddTransient<LogicBlockPage>();
        builder.Services.AddTransient<SettingsPage>();

        return builder.Build();
    }
}
