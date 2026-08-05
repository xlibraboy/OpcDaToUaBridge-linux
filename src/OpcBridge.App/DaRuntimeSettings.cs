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
            updateRateMs,
            useSubscriptions,
            MaxMappedTags: 50000,
            OpcDa: new OpcDaSourceOptions(progId, host, remoteUsername, remotePassword, remoteDomain),
            OpcUa: null,
            Melsec: null,
            S7200: null);
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
                        .Select(SourceConfigMigration.ToDto)
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
        string normalizedSourceId = string.IsNullOrWhiteSpace(sourceId)
            ? DaRuntimeSettings.DefaultSourceId
            : sourceId.Trim();

        return Sources.FirstOrDefault(source =>
            string.Equals(source.SourceId, normalizedSourceId, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record OpcDaSourceOptions(
    string ProgId,
    string Host,
    string? RemoteUsername,
    string? RemotePassword,
    string? RemoteDomain);

public sealed record OpcUaSourceOptions(
    string EndpointUrl,
    string SecurityMode,
    string SecurityPolicy,
    string? Username,
    string? Password,
    int SessionTimeoutMs,
    int ReconnectDelayMs);

public sealed record MelsecA3nSourceOptions(
    string Transport,
    string SerialPortName,
    int BaudRate,
    int DataBits,
    string Parity,
    string StopBits,
    string StationNo,
    string PcNo,
    int TimeoutMs,
    int RetryCount);

public sealed record S7200PpiSourceOptions(
    string Transport,
    string SerialPortName,
    int BaudRate,
    int DataBits,
    string Parity,
    string StopBits,
    int LocalPpiAddress,
    int RemotePpiAddress,
    int TimeoutMs,
    int RetryCount);


public sealed record DaSourceRuntimeSettings(
    string SourceId,
    string DisplayName,
    string SourceType,
    int UpdateRateMs,
    bool UseSubscriptions,
    int MaxMappedTags,
    OpcDaSourceOptions? OpcDa,
    OpcUaSourceOptions? OpcUa,
    MelsecA3nSourceOptions? Melsec,
    S7200PpiSourceOptions? S7200)
{
    // Compat getters — flat access for Program/UI during Phase 1.
    public string ProgId => OpcDa?.ProgId ?? string.Empty;
    public string Host => OpcDa?.Host ?? string.Empty;
    public string? RemoteUsername => OpcDa?.RemoteUsername;
    public string? RemotePassword => OpcDa?.RemotePassword;
    public string? RemoteDomain => OpcDa?.RemoteDomain;
    public string Transport => S7200?.Transport ?? Melsec?.Transport ?? "Serial";
    public string SerialPortName => S7200?.SerialPortName ?? Melsec?.SerialPortName ?? string.Empty;
    public int BaudRate => S7200?.BaudRate ?? Melsec?.BaudRate ?? 9600;
    public int DataBits => S7200?.DataBits ?? Melsec?.DataBits ?? 8;
    public string Parity => S7200?.Parity ?? Melsec?.Parity ?? "Odd";
    public string StopBits => S7200?.StopBits ?? Melsec?.StopBits ?? "One";
    public string StationNo => Melsec?.StationNo ?? "00";
    public string PcNo => Melsec?.PcNo ?? "FF";
    public int TimeoutMs => S7200?.TimeoutMs ?? Melsec?.TimeoutMs ?? 3000;
    public int RetryCount => S7200?.RetryCount ?? Melsec?.RetryCount ?? 2;
    public int LocalPpiAddress => S7200?.LocalPpiAddress ?? 0;
    public int RemotePpiAddress => S7200?.RemotePpiAddress ?? 2;
    public string EndpointUrl => OpcUa?.EndpointUrl ?? string.Empty;
    public string SecurityMode => OpcUa?.SecurityMode ?? "None";
    public string SecurityPolicy => OpcUa?.SecurityPolicy ?? "None";
    public string? UaUsername => OpcUa?.Username;
    public string? UaPassword => OpcUa?.Password;
    public int SessionTimeoutMs => OpcUa?.SessionTimeoutMs ?? 60000;
    public int ReconnectDelayMs => OpcUa?.ReconnectDelayMs ?? 5000;

    public DaClientOptions ToOptions(bool useSubscriptions)
    {
        OpcDaSourceOptions da = OpcDa ?? new OpcDaSourceOptions(string.Empty, "localhost", null, null, null);
        return new DaClientOptions
        {
            SourceId = SourceId,
            DisplayName = DisplayName,
            ProgId = da.ProgId,
            Host = da.Host,
            UpdateRateMs = UpdateRateMs,
            UseSubscriptions = useSubscriptions,
            RemoteUsername = da.RemoteUsername,
            RemotePassword = da.RemotePassword,
            RemoteDomain = da.RemoteDomain
        };
    }

    public OpcBridge.Ua.OpcUaSourceClientOptions ToUaOptions(DaRuntimeSettingsSnapshot settings)
    {
        OpcUaSourceOptions ua = OpcUa ?? new OpcUaSourceOptions(string.Empty, "None", "None", null, null, 60000, 5000);
        bool effectiveUseSubscriptions = settings.UseSubscriptions && UseSubscriptions;
        return new OpcBridge.Ua.OpcUaSourceClientOptions
        {
            SourceId = SourceId,
            DisplayName = DisplayName,
            EndpointUrl = ua.EndpointUrl,
            SecurityMode = ua.SecurityMode,
            SecurityPolicy = ua.SecurityPolicy,
            Username = ua.Username,
            Password = ua.Password,
            UpdateRateMs = UpdateRateMs,
            SessionTimeoutMs = ua.SessionTimeoutMs,
            ReconnectDelayMs = ua.ReconnectDelayMs,
            UseSubscriptions = effectiveUseSubscriptions
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

    // Shared header
    public bool UseSubscriptions { get; set; } = true;
    public int UpdateRateMs { get; set; }
    public int MaxMappedTags { get; set; }

    // Nested (preferred on disk)
    public OpcDaSourceOptionsDto? OpcDa { get; set; }
    public OpcUaSourceOptionsDto? OpcUa { get; set; }
    public MelsecA3nSourceOptionsDto? Melsec { get; set; }
    public S7200PpiSourceOptionsDto? S7200 { get; set; }

    // Legacy flat fields (load only)
    public string? ProgId { get; set; }
    public string? Host { get; set; }
    public string? RemoteUsername { get; set; }
    public string? RemotePassword { get; set; }
    public string? RemoteDomain { get; set; }
    public string? Transport { get; set; }
    public string? SerialPortName { get; set; }
    public int BaudRate { get; set; }
    public int DataBits { get; set; }
    public string? Parity { get; set; }
    public string? StopBits { get; set; }
    public string? StationNo { get; set; }
    public string? PcNo { get; set; }
    public int TimeoutMs { get; set; }
    public int RetryCount { get; set; }
    public string? EndpointUrl { get; set; }
    public string? SecurityMode { get; set; }
    public string? SecurityPolicy { get; set; }
    public string? UaUsername { get; set; }
    public string? UaPassword { get; set; }
    public int SessionTimeoutMs { get; set; }
    public int ReconnectDelayMs { get; set; }
}

public sealed class OpcDaSourceOptionsDto
{
    public string? ProgId { get; set; }
    public string? Host { get; set; }
    public string? RemoteUsername { get; set; }
    public string? RemotePassword { get; set; }
    public string? RemoteDomain { get; set; }
}

public sealed class OpcUaSourceOptionsDto
{
    public string? EndpointUrl { get; set; }
    public string? SecurityMode { get; set; }
    public string? SecurityPolicy { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? UaUsername { get; set; }
    public string? UaPassword { get; set; }
    public int SessionTimeoutMs { get; set; }
    public int ReconnectDelayMs { get; set; }
    public int MaxMappedTags { get; set; }
}

public sealed class MelsecA3nSourceOptionsDto
{
    public string? Transport { get; set; }
    public string? SerialPortName { get; set; }
    public int BaudRate { get; set; }
    public int DataBits { get; set; }
    public string? Parity { get; set; }
    public string? StopBits { get; set; }
    public string? StationNo { get; set; }
    public string? PcNo { get; set; }
    public int TimeoutMs { get; set; }
    public int RetryCount { get; set; }
}

public sealed class S7200PpiSourceOptionsDto
{
    public string? Transport { get; set; }
    public string? SerialPortName { get; set; }
    public int BaudRate { get; set; }
    public int DataBits { get; set; }
    public string? Parity { get; set; }
    public string? StopBits { get; set; }
    public int LocalPpiAddress { get; set; }
    public int RemotePpiAddress { get; set; }
    public int TimeoutMs { get; set; }
    public int RetryCount { get; set; }
}

public static class SourceConfigMigration
{
    public static DaSourceRuntimeSettings FromDto(SourceConfigDto dto, int defaultUpdateRate)
    {
        string sourceType = NormalizeSourceType(dto.SourceType);
        int maxMappedTags = dto.MaxMappedTags;
        if (maxMappedTags <= 0 && dto.OpcUa is not null && dto.OpcUa.MaxMappedTags > 0)
        {
            maxMappedTags = dto.OpcUa.MaxMappedTags;
        }

        OpcDaSourceOptions? opcDa = null;
        OpcUaSourceOptions? opcUa = null;
        MelsecA3nSourceOptions? melsec = null;
        S7200PpiSourceOptions? s7200 = null;

        if (dto.OpcDa is not null)
        {
            opcDa = new OpcDaSourceOptions(
                dto.OpcDa.ProgId ?? string.Empty,
                dto.OpcDa.Host ?? string.Empty,
                dto.OpcDa.RemoteUsername,
                dto.OpcDa.RemotePassword,
                dto.OpcDa.RemoteDomain);
        }
        else if (HasFlatDa(dto))
        {
            opcDa = new OpcDaSourceOptions(
                dto.ProgId ?? string.Empty,
                dto.Host ?? string.Empty,
                dto.RemoteUsername,
                dto.RemotePassword,
                dto.RemoteDomain);
        }

        if (dto.OpcUa is not null)
        {
            opcUa = new OpcUaSourceOptions(
                dto.OpcUa.EndpointUrl ?? string.Empty,
                dto.OpcUa.SecurityMode ?? string.Empty,
                dto.OpcUa.SecurityPolicy ?? string.Empty,
                FirstNonEmpty(dto.OpcUa.Username, dto.OpcUa.UaUsername),
                FirstNonEmpty(dto.OpcUa.Password, dto.OpcUa.UaPassword),
                dto.OpcUa.SessionTimeoutMs,
                dto.OpcUa.ReconnectDelayMs);
        }
        else if (HasFlatUa(dto))
        {
            opcUa = new OpcUaSourceOptions(
                dto.EndpointUrl ?? string.Empty,
                dto.SecurityMode ?? string.Empty,
                dto.SecurityPolicy ?? string.Empty,
                dto.UaUsername,
                dto.UaPassword,
                dto.SessionTimeoutMs,
                dto.ReconnectDelayMs);
        }

        if (dto.Melsec is not null)
        {
            melsec = new MelsecA3nSourceOptions(
                dto.Melsec.Transport ?? string.Empty,
                dto.Melsec.SerialPortName ?? string.Empty,
                dto.Melsec.BaudRate,
                dto.Melsec.DataBits,
                dto.Melsec.Parity ?? string.Empty,
                dto.Melsec.StopBits ?? string.Empty,
                dto.Melsec.StationNo ?? string.Empty,
                dto.Melsec.PcNo ?? string.Empty,
                dto.Melsec.TimeoutMs,
                dto.Melsec.RetryCount);
        }
        else if (HasFlatMelsec(dto)
            && !string.Equals(sourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase)
            && dto.S7200 is null)
        {
            melsec = new MelsecA3nSourceOptions(
                dto.Transport ?? string.Empty,
                dto.SerialPortName ?? string.Empty,
                dto.BaudRate,
                dto.DataBits,
                dto.Parity ?? string.Empty,
                dto.StopBits ?? string.Empty,
                dto.StationNo ?? string.Empty,
                dto.PcNo ?? string.Empty,
                dto.TimeoutMs,
                dto.RetryCount);
        }

        if (dto.S7200 is not null)
        {
            s7200 = new S7200PpiSourceOptions(
                dto.S7200.Transport ?? string.Empty,
                dto.S7200.SerialPortName ?? string.Empty,
                dto.S7200.BaudRate,
                dto.S7200.DataBits,
                dto.S7200.Parity ?? string.Empty,
                dto.S7200.StopBits ?? string.Empty,
                dto.S7200.LocalPpiAddress,
                dto.S7200.RemotePpiAddress,
                dto.S7200.TimeoutMs,
                dto.S7200.RetryCount);
        }
        else if (string.Equals(sourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase)
            && HasFlatSerial(dto))
        {
            s7200 = new S7200PpiSourceOptions(
                dto.Transport ?? string.Empty,
                dto.SerialPortName ?? string.Empty,
                dto.BaudRate,
                dto.DataBits,
                dto.Parity ?? string.Empty,
                dto.StopBits ?? string.Empty,
                0,
                2,
                dto.TimeoutMs,
                dto.RetryCount);
        }

        // Seed missing nest from flat defaults when type is known but nest empty (legacy partial rows).
        if (string.Equals(sourceType, SourceTypes.OpcDa, StringComparison.OrdinalIgnoreCase) && opcDa is null)
        {
            opcDa = new OpcDaSourceOptions(
                dto.ProgId ?? string.Empty,
                dto.Host ?? string.Empty,
                dto.RemoteUsername,
                dto.RemotePassword,
                dto.RemoteDomain);
        }
        else if (string.Equals(sourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase) && opcUa is null)
        {
            opcUa = new OpcUaSourceOptions(
                dto.EndpointUrl ?? string.Empty,
                dto.SecurityMode ?? string.Empty,
                dto.SecurityPolicy ?? string.Empty,
                dto.UaUsername,
                dto.UaPassword,
                dto.SessionTimeoutMs,
                dto.ReconnectDelayMs);
        }
        else if (string.Equals(sourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase) && melsec is null)
        {
            melsec = new MelsecA3nSourceOptions(
                dto.Transport ?? string.Empty,
                dto.SerialPortName ?? string.Empty,
                dto.BaudRate,
                dto.DataBits,
                dto.Parity ?? string.Empty,
                dto.StopBits ?? string.Empty,
                dto.StationNo ?? string.Empty,
                dto.PcNo ?? string.Empty,
                dto.TimeoutMs,
                dto.RetryCount);
        }
        else if (string.Equals(sourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase) && s7200 is null)
        {
            s7200 = new S7200PpiSourceOptions(
                dto.Transport ?? string.Empty,
                dto.SerialPortName ?? string.Empty,
                dto.BaudRate,
                dto.DataBits,
                dto.Parity ?? string.Empty,
                dto.StopBits ?? string.Empty,
                0,
                2,
                dto.TimeoutMs,
                dto.RetryCount);
        }

        return Normalize(new DaSourceRuntimeSettings(
            dto.SourceId ?? DaRuntimeSettings.DefaultSourceId,
            dto.DisplayName ?? string.Empty,
            sourceType,
            dto.UpdateRateMs,
            dto.UseSubscriptions,
            maxMappedTags,
            opcDa,
            opcUa,
            melsec,
            s7200), defaultUpdateRate);
    }

    public static SourceConfigDto ToDto(DaSourceRuntimeSettings source)
    {
        // Persist nested only (no flat driver fields).
        return new SourceConfigDto
        {
            SourceId = source.SourceId,
            DisplayName = source.DisplayName,
            SourceType = source.SourceType,
            UpdateRateMs = source.UpdateRateMs,
            UseSubscriptions = source.UseSubscriptions,
            MaxMappedTags = source.MaxMappedTags,
            OpcDa = source.OpcDa is null ? null : new OpcDaSourceOptionsDto
            {
                ProgId = source.OpcDa.ProgId,
                Host = source.OpcDa.Host,
                RemoteUsername = source.OpcDa.RemoteUsername,
                RemotePassword = source.OpcDa.RemotePassword,
                RemoteDomain = source.OpcDa.RemoteDomain
            },
            OpcUa = source.OpcUa is null ? null : new OpcUaSourceOptionsDto
            {
                EndpointUrl = source.OpcUa.EndpointUrl,
                SecurityMode = source.OpcUa.SecurityMode,
                SecurityPolicy = source.OpcUa.SecurityPolicy,
                Username = source.OpcUa.Username,
                Password = source.OpcUa.Password,
                SessionTimeoutMs = source.OpcUa.SessionTimeoutMs,
                ReconnectDelayMs = source.OpcUa.ReconnectDelayMs
            },
            Melsec = source.Melsec is null ? null : new MelsecA3nSourceOptionsDto
            {
                Transport = source.Melsec.Transport,
                SerialPortName = source.Melsec.SerialPortName,
                BaudRate = source.Melsec.BaudRate,
                DataBits = source.Melsec.DataBits,
                Parity = source.Melsec.Parity,
                StopBits = source.Melsec.StopBits,
                StationNo = source.Melsec.StationNo,
                PcNo = source.Melsec.PcNo,
                TimeoutMs = source.Melsec.TimeoutMs,
                RetryCount = source.Melsec.RetryCount
            },
            S7200 = source.S7200 is null ? null : new S7200PpiSourceOptionsDto
            {
                Transport = source.S7200.Transport,
                SerialPortName = source.S7200.SerialPortName,
                BaudRate = source.S7200.BaudRate,
                DataBits = source.S7200.DataBits,
                Parity = source.S7200.Parity,
                StopBits = source.S7200.StopBits,
                LocalPpiAddress = source.S7200.LocalPpiAddress,
                RemotePpiAddress = source.S7200.RemotePpiAddress,
                TimeoutMs = source.S7200.TimeoutMs,
                RetryCount = source.S7200.RetryCount
            }
        };
    }

    public static DaSourceRuntimeSettings Normalize(DaSourceRuntimeSettings source, int defaultUpdateRate)
    {
        string sourceId = NormalizeSourceId(source.SourceId);
        string displayName = string.IsNullOrWhiteSpace(source.DisplayName) ? sourceId : source.DisplayName.Trim();
        string sourceType = NormalizeSourceType(source.SourceType);
        int updateRateMs = NormalizeUpdateRate(source.UpdateRateMs <= 0 ? defaultUpdateRate : source.UpdateRateMs);
        int maxMappedTags = source.MaxMappedTags <= 0 ? 50000 : Math.Max(1, source.MaxMappedTags);

        OpcDaSourceOptions? opcDa = null;
        OpcUaSourceOptions? opcUa = null;
        MelsecA3nSourceOptions? melsec = null;
        S7200PpiSourceOptions? s7200 = null;

        if (string.Equals(sourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
        {
            OpcUaSourceOptions raw = source.OpcUa ?? new OpcUaSourceOptions(
                source.EndpointUrl,
                source.SecurityMode,
                source.SecurityPolicy,
                source.UaUsername,
                source.UaPassword,
                source.SessionTimeoutMs,
                source.ReconnectDelayMs);

            opcUa = new OpcUaSourceOptions(
                raw.EndpointUrl?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(raw.SecurityMode) ? "None" : raw.SecurityMode.Trim(),
                string.IsNullOrWhiteSpace(raw.SecurityPolicy) ? "None" : raw.SecurityPolicy.Trim(),
                string.IsNullOrWhiteSpace(raw.Username) ? null : raw.Username.Trim(),
                string.IsNullOrWhiteSpace(raw.Password) ? null : raw.Password,
                raw.SessionTimeoutMs <= 0 ? 60000 : raw.SessionTimeoutMs,
                raw.ReconnectDelayMs <= 0 ? 5000 : raw.ReconnectDelayMs);
        }
        else if (string.Equals(sourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
        {
            MelsecA3nSourceOptions raw = source.Melsec ?? new MelsecA3nSourceOptions(
                source.Transport,
                source.SerialPortName,
                source.BaudRate,
                source.DataBits,
                source.Parity,
                source.StopBits,
                source.StationNo,
                source.PcNo,
                source.TimeoutMs,
                source.RetryCount);

            melsec = new MelsecA3nSourceOptions(
                string.IsNullOrWhiteSpace(raw.Transport) ? "Serial" : raw.Transport.Trim(),
                raw.SerialPortName?.Trim() ?? string.Empty,
                raw.BaudRate > 0 ? raw.BaudRate : 9600,
                raw.DataBits is 7 or 8 ? raw.DataBits : 8,
                string.IsNullOrWhiteSpace(raw.Parity) ? "Odd" : raw.Parity.Trim(),
                string.IsNullOrWhiteSpace(raw.StopBits) ? "One" : raw.StopBits.Trim(),
                string.IsNullOrWhiteSpace(raw.StationNo) ? "00" : raw.StationNo.Trim(),
                string.IsNullOrWhiteSpace(raw.PcNo) ? "FF" : raw.PcNo.Trim(),
                raw.TimeoutMs <= 0 ? 3000 : raw.TimeoutMs,
                raw.RetryCount < 0 ? 2 : raw.RetryCount);
        }
        else if (string.Equals(sourceType, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
        {
            S7200PpiSourceOptions raw = source.S7200 ?? new S7200PpiSourceOptions(
                source.Transport,
                source.SerialPortName,
                source.BaudRate,
                source.DataBits,
                source.Parity,
                source.StopBits,
                source.LocalPpiAddress,
                source.RemotePpiAddress,
                source.TimeoutMs,
                source.RetryCount);

            s7200 = new S7200PpiSourceOptions(
                string.IsNullOrWhiteSpace(raw.Transport) ? "Serial" : raw.Transport.Trim(),
                raw.SerialPortName?.Trim() ?? string.Empty,
                raw.BaudRate > 0 ? raw.BaudRate : 9600,
                raw.DataBits is 7 or 8 ? raw.DataBits : 8,
                string.IsNullOrWhiteSpace(raw.Parity) ? "Even" : raw.Parity.Trim(),
                string.IsNullOrWhiteSpace(raw.StopBits) ? "One" : raw.StopBits.Trim(),
                raw.LocalPpiAddress < 0 ? 0 : raw.LocalPpiAddress,
                raw.RemotePpiAddress < 0 || raw.RemotePpiAddress > 126 ? 2 : raw.RemotePpiAddress,
                raw.TimeoutMs <= 0 ? 3000 : raw.TimeoutMs,
                raw.RetryCount <= 0 ? 2 : raw.RetryCount);
        }
        else
        {
            // OpcDa (default / unknown collapsed)
            OpcDaSourceOptions raw = source.OpcDa ?? new OpcDaSourceOptions(
                source.ProgId,
                source.Host,
                source.RemoteUsername,
                source.RemotePassword,
                source.RemoteDomain);

            opcDa = new OpcDaSourceOptions(
                raw.ProgId?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(raw.Host) ? "localhost" : raw.Host.Trim(),
                string.IsNullOrWhiteSpace(raw.RemoteUsername) ? null : raw.RemoteUsername.Trim(),
                string.IsNullOrWhiteSpace(raw.RemotePassword) ? null : raw.RemotePassword,
                string.IsNullOrWhiteSpace(raw.RemoteDomain) ? null : raw.RemoteDomain.Trim());
        }

        return new DaSourceRuntimeSettings(
            sourceId,
            displayName,
            sourceType,
            updateRateMs,
            source.UseSubscriptions,
            maxMappedTags,
            opcDa,
            opcUa,
            melsec,
            s7200);
    }

    private static bool HasFlatDa(SourceConfigDto dto) =>
        !string.IsNullOrWhiteSpace(dto.ProgId) ||
        !string.IsNullOrWhiteSpace(dto.Host) ||
        !string.IsNullOrWhiteSpace(dto.RemoteUsername) ||
        !string.IsNullOrWhiteSpace(dto.RemoteDomain);

    private static bool HasFlatUa(SourceConfigDto dto) =>
        !string.IsNullOrWhiteSpace(dto.EndpointUrl) ||
        !string.IsNullOrWhiteSpace(dto.SecurityMode) ||
        !string.IsNullOrWhiteSpace(dto.SecurityPolicy) ||
        !string.IsNullOrWhiteSpace(dto.UaUsername) ||
        dto.SessionTimeoutMs > 0 ||
        dto.ReconnectDelayMs > 0;

    private static bool HasFlatMelsec(SourceConfigDto dto) =>
        !string.IsNullOrWhiteSpace(dto.SerialPortName) ||
        !string.IsNullOrWhiteSpace(dto.Transport) ||
        dto.BaudRate > 0 ||
        dto.DataBits > 0 ||
        !string.IsNullOrWhiteSpace(dto.Parity) ||
        !string.IsNullOrWhiteSpace(dto.StopBits) ||
        !string.IsNullOrWhiteSpace(dto.StationNo) ||
        !string.IsNullOrWhiteSpace(dto.PcNo) ||
        dto.TimeoutMs > 0 ||
        dto.RetryCount > 0;

    private static string? FirstNonEmpty(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a : (!string.IsNullOrWhiteSpace(b) ? b : null);

    private static string NormalizeSourceType(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return SourceTypes.OpcDa;
        }

        string trimmed = sourceType.Trim();
        if (string.Equals(trimmed, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
        {
            return SourceTypes.MelsecA3n;
        }

        if (string.Equals(trimmed, SourceTypes.S7200Ppi, StringComparison.OrdinalIgnoreCase))
        {
            return SourceTypes.S7200Ppi;
        }

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

    private static bool HasFlatSerial(SourceConfigDto dto) =>
        !string.IsNullOrWhiteSpace(dto.SerialPortName) ||
        !string.IsNullOrWhiteSpace(dto.Transport) ||
        dto.BaudRate > 0 ||
        dto.DataBits > 0 ||
        !string.IsNullOrWhiteSpace(dto.Parity) ||
        !string.IsNullOrWhiteSpace(dto.StopBits) ||
        dto.TimeoutMs > 0 ||
        dto.RetryCount > 0;
}
