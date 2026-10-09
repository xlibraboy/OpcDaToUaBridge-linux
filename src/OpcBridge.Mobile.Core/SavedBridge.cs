using System.Text.Json;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// One bridge saved on the phone: a stable id (the key its tags live under in the shared
/// cache), the name the operator sees, and the address that answered the probe.
/// </summary>
public sealed record SavedBridge(string Id, string Name, string Url)
{
    /// <summary>What the pickers show: the operator's name, else the address.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Url : Name;
}

/// <summary>Where the saved-bridge list lives on the phone (Preferences on Android).</summary>
public interface IBridgeSettings
{
    string Get(string key, string fallback);

    void Set(string key, string value);
}

/// <summary>
/// The saved-bridge list as it is stored: a JSON array under one key. A store that cannot be
/// read (first run, a truncated write) reads as an empty list, never as an exception — the
/// phone is expected to be handed any state.
/// </summary>
public static class BridgeListCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(IEnumerable<SavedBridge> bridges) =>
        JsonSerializer.Serialize(bridges.ToList(), JsonOptions);

    public static IReadOnlyList<SavedBridge> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SavedBridge>();
        }

        try
        {
            List<SavedBridge>? bridges = JsonSerializer.Deserialize<List<SavedBridge>>(json, JsonOptions);
            return bridges?.Where(IsUsable).ToList() ?? (IReadOnlyList<SavedBridge>)Array.Empty<SavedBridge>();
        }
        catch (JsonException)
        {
            return Array.Empty<SavedBridge>();
        }
    }

    /// <summary>A short, stable id (the tag cache keys every bridge by it).</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>"10.3.1.50:8080" for a saved address, used when the operator names nothing.</summary>
    public static string DefaultName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            return url;
        }

        return parsed.IsDefaultPort ? parsed.Host : $"{parsed.Host}:{parsed.Port}";
    }

    /// <summary>Same address, so a bridge added twice is one entry.</summary>
    public static bool SameAddress(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string url) => (url ?? string.Empty).Trim().TrimEnd('/');

    private static bool IsUsable(SavedBridge bridge) =>
        !string.IsNullOrWhiteSpace(bridge.Id)
        && !string.IsNullOrWhiteSpace(bridge.Url)
        && bridge.Url.Contains("://", StringComparison.Ordinal);
}
