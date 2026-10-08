using Microsoft.Extensions.Logging;
using OpcBridge.Mobile.Core;
using OpcBridge.Mobile.Views;

namespace OpcBridge.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<LogicApiClient>();
        builder.Services.AddSingleton<MobileHubClient>();
        builder.Services.AddSingleton<AppState>();
        builder.Services.AddTransient<LogicPage>();
        builder.Services.AddTransient<LogicBlockPage>();
        builder.Services.AddTransient<SettingsPage>();

        return builder.Build();
    }
}
