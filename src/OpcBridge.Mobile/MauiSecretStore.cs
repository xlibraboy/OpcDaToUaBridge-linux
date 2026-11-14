using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile;

/// <summary>
/// The plant AP passphrase on Android's SecureStorage (Keystore-backed), kept apart from the
/// ordinary preferences that hold the SSID/BSSID. A missing or unreadable entry reads as no
/// password; the bind flow asks for it again.
/// </summary>
public sealed class MauiSecretStore : ISecretStore
{
    public string? Get(string key)
    {
        try
        {
            return Task.Run(() => SecureStorage.Default.GetAsync(key)).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Set(string key, string value)
    {
        try
        {
            Task.Run(() => SecureStorage.Default.SetAsync(key, value)).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }
    }

    public void Remove(string key)
    {
        try
        {
            SecureStorage.Default.Remove(key);
        }
        catch (Exception)
        {
        }
    }
}
