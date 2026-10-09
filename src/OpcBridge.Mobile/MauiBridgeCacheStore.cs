using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile;

/// <summary>
/// The phone's offline cache: one JSON file per key under the app's data directory, so the
/// logic list survives a lost link, a bridge restart and an app restart.
/// </summary>
public sealed class MauiBridgeCacheStore : IBridgeCacheStore
{
    public string? Read(string key)
    {
        try
        {
            string path = PathFor(key);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Write(string key, string value)
    {
        try
        {
            File.WriteAllText(PathFor(key), value);
        }
        catch
        {
        }
    }

    private static string PathFor(string key)
    {
        string name = new(key.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        return Path.Combine(FileSystem.AppDataDirectory, "opcbridge-cache-" + name + ".json");
    }
}
