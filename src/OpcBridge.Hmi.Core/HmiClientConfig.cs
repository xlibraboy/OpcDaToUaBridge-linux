using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpcBridge.Hmi.Core;

public sealed class HmiClientConfig
{
    public string DisplayStoreUrl { get; set; } = "http://127.0.0.1:8080";
    public List<HmiBridgeEndpoint> Bridges { get; set; } = new();
    public string? StartupDisplayId { get; set; }

    public static HmiClientConfig CreateDefaultSingleBridge(string baseUrl = "http://127.0.0.1:8080")
    {
        string url = string.IsNullOrWhiteSpace(baseUrl) ? "http://127.0.0.1:8080" : baseUrl.Trim().TrimEnd('/');
        return new HmiClientConfig
        {
            DisplayStoreUrl = url,
            Bridges =
            [
                new HmiBridgeEndpoint
                {
                    Id = "default",
                    BaseUrl = url,
                    Enabled = true
                }
            ]
        };
    }

    public IEnumerable<HmiBridgeEndpoint> EnabledBridges() =>
        Bridges.Where(b => b.Enabled && !string.IsNullOrWhiteSpace(b.Id) && !string.IsNullOrWhiteSpace(b.BaseUrl));

    public bool TryGetBridge(string bridgeId, out HmiBridgeEndpoint? endpoint)
    {
        endpoint = Bridges.FirstOrDefault(b =>
            string.Equals(b.Id, bridgeId, StringComparison.OrdinalIgnoreCase));
        return endpoint is not null;
    }

    public static HmiClientConfig LoadOrDefault(string path, string? fallbackBaseUrl = null)
    {
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                HmiClientConfig? loaded = JsonSerializer.Deserialize<HmiClientConfig>(json, JsonOptions);
                if (loaded is not null)
                {
                    Normalize(loaded);
                    if (loaded.Bridges.Count == 0)
                    {
                        return CreateDefaultSingleBridge(fallbackBaseUrl ?? loaded.DisplayStoreUrl);
                    }

                    return loaded;
                }
            }
            catch
            {
                // fall through to default
            }
        }

        return CreateDefaultSingleBridge(fallbackBaseUrl ?? "http://127.0.0.1:8080");
    }

    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        Normalize(this);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static void Normalize(HmiClientConfig config)
    {
        config.DisplayStoreUrl = (config.DisplayStoreUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(config.DisplayStoreUrl))
        {
            config.DisplayStoreUrl = "http://127.0.0.1:8080";
        }

        foreach (HmiBridgeEndpoint bridge in config.Bridges)
        {
            bridge.Id = (bridge.Id ?? string.Empty).Trim();
            bridge.BaseUrl = (bridge.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(config.StartupDisplayId))
        {
            config.StartupDisplayId = config.StartupDisplayId.Trim();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class HmiBridgeEndpoint
{
    public string Id { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
