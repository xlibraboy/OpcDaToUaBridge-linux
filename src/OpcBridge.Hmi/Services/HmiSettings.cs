using System.Text.Json;

namespace OpcBridge.Hmi.Services;

/// <summary>
/// Minimal settings persistence for the HMI. Stores the last known bridge base
/// URL so the auto-discovery scan can be skipped when the bridge is still on
/// the same port as the previous session.
/// </summary>
public static class HmiSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string SettingsPath => Path.Combine(
        AppContext.BaseDirectory,
        "hmi-settings.json");

    /// <summary>Returns the saved base URL, or null when none has been persisted yet.</summary>
    public static string? LoadBaseUrl()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            string json = File.ReadAllText(SettingsPath);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("baseUrl", out JsonElement url) &&
                url.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(url.GetString()))
            {
                return url.GetString();
            }
        }
        catch
        {
            // corrupt settings are ignored
        }

        return null;
    }

    public static void SaveBaseUrl(string baseUrl)
    {
        try
        {
            string json = JsonSerializer.Serialize(
                new { baseUrl },
                JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // persistence is best-effort
        }
    }
}
