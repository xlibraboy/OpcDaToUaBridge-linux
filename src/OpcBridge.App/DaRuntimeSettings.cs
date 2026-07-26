using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.Core;
using OpcBridge.Da;

namespace OpcBridge.App;

public sealed class DaRuntimeSettings
{
    public const string DefaultSourceId = "default";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object sync_ = new();
    private readonly string persist_path_;
    private DaRuntimeSettingsSnapshot snapshot_;

    public DaRuntimeSettings(IOptions<DaClientOptions> options)
    {
        persist_path_ = Path.Combine(AppContext.BaseDirectory, "sources.json");

        // Load from sources.json if it exists; otherwise seed from appsettings.json.
        DaRuntimeSettingsSnapshot? loaded = LoadFromDisk();
        if (loaded is not null)
        {
            snapshot_ = loaded;
        }
        else
        {
            snapshot_ = new DaRuntimeSettingsSnapshot(
                NormalizeUpdateRate(options.Value.UpdateRateMs),
                options.Value.UseSubscriptions,
                BuildInitialSources(options.Value),
                0);
        }
    }

    public DaRuntimeSettingsSnapshot GetSnapshot()
    {
        lock (sync_)
        {
            return snapshot_;
        }
    }

    public DaRuntimeSettingsSnapshot UpsertSource(DaSourceRuntimeSettings source)
    {
        DaSourceRuntimeSettings normalized = NormalizeSource(source, snapshot_.UpdateRateMs);

        lock (sync_)
        {
            List<DaSourceRuntimeSettings> sources = snapshot_.Sources.ToList();
            int index = sources.FindIndex(existing =>
                string.Equals(existing.SourceId, normalized.SourceId, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                sources[index] = normalized;
            }
            else
            {
                sources.Add(normalized);
            }

            snapshot_ = snapshot_ with
            {
                Sources = sources,
                Version = snapshot_.Version + 1
            };

            Persist();
            return snapshot_;
        }
    }

    public bool TryRemoveSource(string sourceId, out DaRuntimeSettingsSnapshot snapshot)
    {
        string normalizedSourceId = NormalizeSourceId(sourceId);

        lock (sync_)
        {
            if (snapshot_.Sources.Count <= 1)
            {
                snapshot = snapshot_;
                return false;
            }

            List<DaSourceRuntimeSettings> sources = snapshot_.Sources
                .Where(source => !string.Equals(source.SourceId, normalizedSourceId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sources.Count == snapshot_.Sources.Count)
            {
                snapshot = snapshot_;
                return false;
            }

            snapshot_ = snapshot_ with
            {
                Sources = sources,
                Version = snapshot_.Version + 1
            };

            Persist();
            snapshot = snapshot_;
            return true;
        }
    }
    public DaRuntimeSettingsSnapshot SetUpdateRate(int updateRateMs)
    {
        int normalizedUpdateRate = NormalizeUpdateRate(updateRateMs);

        lock (sync_)
        {
            snapshot_ = snapshot_ with
            {
                UpdateRateMs = normalizedUpdateRate,
                Version = snapshot_.Version + 1
            };

            Persist();
            return snapshot_;
        }
    }

    public DaRuntimeSettingsSnapshot SetSourceUpdateRate(string sourceId, int updateRateMs)
    {
        int normalizedRate = NormalizeUpdateRate(updateRateMs);

        lock (sync_)
        {
            List<DaSourceRuntimeSettings> sources = snapshot_.Sources.ToList();
            int index = sources.FindIndex(source =>
                string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return snapshot_;
            }

            sources[index] = sources[index] with { UpdateRateMs = normalizedRate };
            snapshot_ = snapshot_ with
            {
                Sources = sources,
                Version = snapshot_.Version + 1
            };

            Persist();
            return snapshot_;
        }
    }

    public DaRuntimeSettingsSnapshot SetUseSubscriptions(bool enabled)
    {
        lock (sync_)
        {
            snapshot_ = snapshot_ with
            {
                UseSubscriptions = enabled,
                Version = snapshot_.Version + 1
            };
            Persist();
            return snapshot_;
        }
    }


    public DaRuntimeSettingsSnapshot SetServerConfig(
        string progId,
        string host,
        string? username = null,
        string? password = null,
        string? domain = null)
    {
        return UpsertSource(CreateDaSource(
            DefaultSourceId,
            "Default Source",
            progId,
            host,
            username,
            password,
            domain,
            0));
    }

    private static IReadOnlyList<DaSourceRuntimeSettings> BuildInitialSources(DaClientOptions options)
    {
        int defaultRate = NormalizeUpdateRate(options.UpdateRateMs);
        List<DaSourceRuntimeSettings> configuredSources = new();

        if (options.Sources is { Count: > 0 })
        {
            foreach (DaSourceOptions source in options.Sources)
            {
                configuredSources.Add(NormalizeSource(CreateDaSource(
                    source.SourceId,
                    source.DisplayName,
                    source.ProgId,
                    source.Host,
                    source.RemoteUsername,
                    source.RemotePassword,
                    source.RemoteDomain,
                    source.UpdateRateMs), defaultRate));
            }
        }
        else
        {
            configuredSources.Add(NormalizeSource(CreateDaSource(
                string.IsNullOrWhiteSpace(options.SourceId) ? DefaultSourceId : options.SourceId,
                string.IsNullOrWhiteSpace(options.DisplayName) ? "Default Source" : options.DisplayName,
                options.ProgId,
                options.Host,
                options.RemoteUsername,
                options.RemotePassword,
                options.RemoteDomain,
                options.UpdateRateMs), defaultRate));
        }

        List<DaSourceRuntimeSettings> dedupedSources = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < configuredSources.Count; i++)
        {
            DaSourceRuntimeSettings source = configuredSources[i];
            if (seen.Add(source.SourceId))
            {
                dedupedSources.Add(source);
            }
        }

        if (dedupedSources.Count == 0)
        {
            dedupedSources.Add(NormalizeSource(CreateDaSource(
                DefaultSourceId,
                "Default Source",
                string.Empty,
                "localhost",
                null,
                null,
                null,
                NormalizeUpdateRate(0)), defaultRate));
        }

        return dedupedSources;
    }

    internal static DaSourceRuntimeSettings CreateDaSource(
        string sourceId,
        string displayName,
        string progId,
        string host,
        string? remoteUsername,
        string? remotePassword,
        string? remoteDomain,
        int updateRateMs,
        bool useSubscriptions = true)
    {
        return new DaSourceRuntimeSettings(
            sourceId,
            displayName,
            SourceTypes.OpcDa,
            progId,
            host,
            remoteUsername,
            remotePassword,
            remoteDomain,
            string.Empty,
            "None",
            "None",
            null,
            null,
            60000,
            5000,
            50000,
            useSubscriptions,
            updateRateMs);
    }

    private static DaSourceRuntimeSettings NormalizeSource(DaSourceRuntimeSettings source, int defaultUpdateRate)
    {
        return SourceConfigMigration.Normalize(source, defaultUpdateRate);
    }

    private static string NormalizeSourceId(string? sourceId)
    {
        string value = sourceId?.Trim() ?? string.Empty;
        return value.Length == 0 ? DefaultSourceId : value;
    }

    private static int NormalizeUpdateRate(int updateRateMs)
    {
        if (updateRateMs <= 0)
        {
            return 1000;
        }

        return Math.Max(100, updateRateMs);
    }
    private void Persist()
    {
        try
        {
            lock (sync_)
            {
                var dto = new SourcesConfigDto
                {
                    UpdateRateMs = snapshot_.UpdateRateMs,
                    UseSubscriptions = snapshot_.UseSubscriptions,
                    Sources = snapshot_.Sources
                        .Select(s => new SourceConfigDto
                        {
                            SourceId = s.SourceId,
                            DisplayName = s.DisplayName,
                            SourceType = s.SourceType,
                            ProgId = s.ProgId,
                            Host = s.Host,
                            RemoteUsername = s.RemoteUsername,
                            RemotePassword = s.RemotePassword,
                            RemoteDomain = s.RemoteDomain,
                            EndpointUrl = s.EndpointUrl,
                            SecurityMode = s.SecurityMode,
                            SecurityPolicy = s.SecurityPolicy,
                            UaUsername = s.UaUsername,
                            UaPassword = s.UaPassword,
                            SessionTimeoutMs = s.SessionTimeoutMs,
                            ReconnectDelayMs = s.ReconnectDelayMs,
                            MaxMappedTags = s.MaxMappedTags,
                            UseSubscriptions = s.UseSubscriptions,
                            UpdateRateMs = s.UpdateRateMs
                        })
                        .ToList()
                };
                string json = JsonSerializer.Serialize(dto, JsonOptions);
                File.WriteAllText(persist_path_, json);
            }
        }
        catch
        {
        }
    }

    private DaRuntimeSettingsSnapshot? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(persist_path_)) return null;
            string json = File.ReadAllText(persist_path_);
            SourcesConfigDto? dto = JsonSerializer.Deserialize<SourcesConfigDto>(json);
            if (dto is null) return null;

            int defaultRate = NormalizeUpdateRate(dto.UpdateRateMs);
            List<DaSourceRuntimeSettings> sources = dto.Sources?
                .Select(s => SourceConfigMigration.FromDto(s, defaultRate))
                .ToList() ?? new List<DaSourceRuntimeSettings>();

            if (sources.Count == 0) return null;

            return new DaRuntimeSettingsSnapshot(defaultRate, dto.UseSubscriptions, sources, 0);
        }
        catch
        {
            return null;
        }
    }

    public void RestoreFromSnapshot(DaRuntimeSettingsSnapshot snapshot)
    {
        lock (sync_)
        {
            int defaultRate = NormalizeUpdateRate(snapshot.UpdateRateMs);
            IReadOnlyList<DaSourceRuntimeSettings> normalizedSources = snapshot.Sources
                .Select(source => NormalizeSource(source, defaultRate))
                .ToList();

            snapshot_ = snapshot with
            {
                UpdateRateMs = defaultRate,
                Sources = normalizedSources,
                Version = snapshot_.Version + 1
            };
            Persist();
        }
    }

}

public sealed record DaRuntimeSettingsSnapshot(
    int UpdateRateMs,
    bool UseSubscriptions,
    IReadOnlyList<DaSourceRuntimeSettings> Sources,
    long Version)
{
    public DaSourceRuntimeSettings? GetSource(string? sourceId)
    {
        string normalized = string.IsNullOrWhiteSpace(sourceId)
            ? DaRuntimeSettings.DefaultSourceId
            : sourceId.Trim();

        return Sources.FirstOrDefault(source =>
            string.Equals(source.SourceId, normalized, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record DaSourceRuntimeSettings(
    string SourceId,
    string DisplayName,
    string SourceType,
    string ProgId,
    string Host,
    string? RemoteUsername,
    string? RemotePassword,
    string? RemoteDomain,
    string EndpointUrl,
    string SecurityMode,
    string SecurityPolicy,
    string? UaUsername,
    string? UaPassword,
    int SessionTimeoutMs,
    int ReconnectDelayMs,
    int MaxMappedTags,
    bool UseSubscriptions,
    int UpdateRateMs)
{
    public DaClientOptions ToOptions(bool useSubscriptions)
    {
        return new DaClientOptions
        {
            SourceId = SourceId,
            DisplayName = DisplayName,
            ProgId = ProgId,
            Host = Host,
            UpdateRateMs = UpdateRateMs,
            UseSubscriptions = useSubscriptions,
            RemoteUsername = RemoteUsername,
            RemotePassword = RemotePassword,
            RemoteDomain = RemoteDomain
        };
    }

    public OpcBridge.Ua.OpcUaSourceClientOptions ToUaOptions(DaRuntimeSettingsSnapshot settings)
    {
        bool useSubscriptions = settings.UseSubscriptions && UseSubscriptions;
        return new OpcBridge.Ua.OpcUaSourceClientOptions
        {
            SourceId = SourceId,
            DisplayName = DisplayName,
            EndpointUrl = EndpointUrl,
            SecurityMode = SecurityMode,
            SecurityPolicy = SecurityPolicy,
            Username = UaUsername,
            Password = UaPassword,
            UpdateRateMs = UpdateRateMs,
            SessionTimeoutMs = SessionTimeoutMs,
            ReconnectDelayMs = ReconnectDelayMs,
            UseSubscriptions = useSubscriptions
        };
    }
}

internal sealed class SourcesConfigDto
{
    public int UpdateRateMs { get; set; } = 1000;
    public bool UseSubscriptions { get; set; } = true;
    public List<SourceConfigDto> Sources { get; set; } = new();
}

public sealed class SourceConfigDto
{
    public string? SourceId { get; set; }
    public string? DisplayName { get; set; }
    public string? SourceType { get; set; }
    public string? ProgId { get; set; }
    public string? Host { get; set; }
    public string? RemoteUsername { get; set; }
    public string? RemotePassword { get; set; }
    public string? RemoteDomain { get; set; }
    public string? EndpointUrl { get; set; }
    public string? SecurityMode { get; set; }
    public string? SecurityPolicy { get; set; }
    public string? UaUsername { get; set; }
    public string? UaPassword { get; set; }
    public int SessionTimeoutMs { get; set; }
    public int ReconnectDelayMs { get; set; }
    public int MaxMappedTags { get; set; }
    public bool? UseSubscriptions { get; set; }
    public int UpdateRateMs { get; set; }
}

public static class SourceConfigMigration
{
    public static DaSourceRuntimeSettings FromDto(SourceConfigDto dto, int defaultUpdateRate)
    {
        return Normalize(new DaSourceRuntimeSettings(
            dto.SourceId ?? DaRuntimeSettings.DefaultSourceId,
            dto.DisplayName ?? string.Empty,
            dto.SourceType ?? string.Empty,
            dto.ProgId ?? string.Empty,
            dto.Host ?? string.Empty,
            dto.RemoteUsername,
            dto.RemotePassword,
            dto.RemoteDomain,
            dto.EndpointUrl ?? string.Empty,
            dto.SecurityMode ?? string.Empty,
            dto.SecurityPolicy ?? string.Empty,
            dto.UaUsername,
            dto.UaPassword,
            dto.SessionTimeoutMs,
            dto.ReconnectDelayMs,
            dto.MaxMappedTags,
            dto.UseSubscriptions ?? true,
            dto.UpdateRateMs), defaultUpdateRate);
    }

    public static DaSourceRuntimeSettings Normalize(DaSourceRuntimeSettings source, int defaultUpdateRate)
    {
        string sourceId = NormalizeSourceId(source.SourceId);
        string displayName = string.IsNullOrWhiteSpace(source.DisplayName) ? sourceId : source.DisplayName.Trim();
        string sourceType = NormalizeSourceType(source.SourceType);
        int updateRateMs = NormalizeUpdateRate(source.UpdateRateMs <= 0 ? defaultUpdateRate : source.UpdateRateMs);
        int sessionTimeoutMs = source.SessionTimeoutMs <= 0 ? 60000 : source.SessionTimeoutMs;
        int reconnectDelayMs = source.ReconnectDelayMs <= 0 ? 5000 : source.ReconnectDelayMs;
        int maxMappedTags = source.MaxMappedTags <= 0 ? 50000 : Math.Max(1, source.MaxMappedTags);
        string securityMode = string.IsNullOrWhiteSpace(source.SecurityMode) ? "None" : source.SecurityMode.Trim();
        string securityPolicy = string.IsNullOrWhiteSpace(source.SecurityPolicy) ? "None" : source.SecurityPolicy.Trim();
        string endpointUrl = source.EndpointUrl?.Trim() ?? string.Empty;
        string host = string.IsNullOrWhiteSpace(source.Host) ? "localhost" : source.Host.Trim();

        return new DaSourceRuntimeSettings(
            sourceId,
            displayName,
            sourceType,
            source.ProgId?.Trim() ?? string.Empty,
            host,
            string.IsNullOrWhiteSpace(source.RemoteUsername) ? null : source.RemoteUsername.Trim(),
            string.IsNullOrWhiteSpace(source.RemotePassword) ? null : source.RemotePassword,
            string.IsNullOrWhiteSpace(source.RemoteDomain) ? null : source.RemoteDomain.Trim(),
            endpointUrl,
            securityMode,
            securityPolicy,
            string.IsNullOrWhiteSpace(source.UaUsername) ? null : source.UaUsername.Trim(),
            string.IsNullOrWhiteSpace(source.UaPassword) ? null : source.UaPassword,
            sessionTimeoutMs,
            reconnectDelayMs,
            maxMappedTags,
            source.UseSubscriptions,
            updateRateMs);
    }

    private static string NormalizeSourceType(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return SourceTypes.OpcDa;
        }

        string trimmed = sourceType.Trim();
        if (string.Equals(trimmed, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
        {
            return SourceTypes.OpcUa;
        }

        if (string.Equals(trimmed, SourceTypes.OpcDa, StringComparison.OrdinalIgnoreCase))
        {
            return SourceTypes.OpcDa;
        }

        // Load resilience: unknown types collapse to OpcDa; API validates on write.
        return SourceTypes.OpcDa;
    }

    private static string NormalizeSourceId(string? sourceId)
    {
        string value = sourceId?.Trim() ?? string.Empty;
        return value.Length == 0 ? DaRuntimeSettings.DefaultSourceId : value;
    }

    private static int NormalizeUpdateRate(int updateRateMs)
    {
        if (updateRateMs <= 0)
        {
            return 1000;
        }

        return Math.Max(100, updateRateMs);
    }
}
