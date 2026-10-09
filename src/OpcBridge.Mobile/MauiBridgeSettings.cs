using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile;

/// <summary>The saved-bridge store on Android: MAUI Preferences.</summary>
public sealed class MauiBridgeSettings : IBridgeSettings
{
    public string Get(string key, string fallback) => Preferences.Default.Get(key, fallback);

    public void Set(string key, string value) => Preferences.Default.Set(key, value);
}
