using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
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
        builder.Services.AddSingleton<ISecretStore, MauiSecretStore>();
        builder.Services.AddSingleton<IAccessPointNetwork, AndroidAccessPointNetwork>();
        builder.Services.AddSingleton(services => new AccessPointGate(
            services.GetRequiredService<IBridgeSettings>(),
            services.GetRequiredService<ISecretStore>(),
            services.GetRequiredService<IAccessPointNetwork>()));
        // The phone's own copy of every bridge's synced dataset, in a SQLite database beside
        // the app's data. One file survives app restarts and releases.
        builder.Services.AddSingleton<IMobileStore>(_ => new SqliteMobileStore(
            Path.Combine(FileSystem.AppDataDirectory, "opcbridge-logic.db")));
        builder.Services.AddSingleton<MultiBridgeTagCache>();
        builder.Services.AddSingleton(services => new BridgeCoordinator(
            services.GetRequiredService<IBridgeSettings>(),
            services.GetRequiredService<MultiBridgeTagCache>(),
            action => MainThread.BeginInvokeOnMainThread(action),
            services.GetRequiredService<IMobileStore>(),
            services.GetRequiredService<AccessPointGate>()));
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<LogicPage>();
        builder.Services.AddTransient<LogicBlockPage>();
        builder.Services.AddTransient<ElementsPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<AccessPointPage>();

#if ANDROID
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("PlantField", (handler, _) =>
        {
            if (handler.PlatformView is Android.Widget.EditText editText)
            {
                StyleField(editText);
            }
        });
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("PlantField", (handler, _) =>
        {
            if (handler.PlatformView is Android.Widget.EditText editText)
            {
                StyleField(editText);
            }
        });
#endif

        return builder.Build();
    }

#if ANDROID
    private static void StyleField(Android.Widget.EditText editText)
    {
        editText.BackgroundTintList =
            Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
        editText.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
    }

    private static int Dp(float value) =>
        (int)(Android.App.Application.Context.Resources!.DisplayMetrics!.Density * value + 0.5f);
#endif
}
