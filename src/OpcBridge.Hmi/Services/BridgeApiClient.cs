using System.Net.Http.Json;
using System.Text.Json;
using OpcBridge.Client;

namespace OpcBridge.Hmi.Services;

/// <summary>Runtime port info from GET /api/status/ports.</summary>
public sealed record HmiPortInfo(
    int HttpPort,
    int UaPort,
    int HttpDefault,
    int UaDefault,
    bool HttpAutoAssigned,
    bool UaAutoAssigned,
    string? UaEndpointClient);

/// <summary>Live InfluxDB writer state from GET /api/influx/status.</summary>
public sealed record HmiInfluxStatus(
    string State,
    string? LastError,
    long WrittenCount,
    double WrittenRate,
    bool Enabled)
{
    public bool IsConnected => string.Equals(State, "Connected", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Plant-logic definitions and their version, from GET /api/logic.</summary>
public sealed record HmiLogicDefinitions(
    IReadOnlyList<LogicBlockDto> Blocks,
    IReadOnlyList<LogicRelayDto> Relays,
    long Version);

/// <summary>Outcome of POST /api/logic/blocks or DELETE /api/logic/blocks/{id}.</summary>
public sealed record HmiLogicSaveResult(bool Ok, LogicBlockDto? Block, long Version, string? Error);

/// <summary>Outcome of POST /api/logic/relays — the relay/tag bindings the editor saved.</summary>
public sealed record HmiLogicRelaySaveResult(bool Ok, IReadOnlyList<LogicRelayDto>? Relays, long Version, string? Error);

/// <summary>Wire shape of a successful POST /api/logic/blocks.</summary>
internal sealed class HmiLogicSaveResponse
{
    public LogicBlockDto? Block { get; set; }

    public long Version { get; set; }
}

/// <summary>Wire shape of a successful DELETE /api/logic/blocks/{id}.</summary>
internal sealed class HmiLogicVersionResponse
{
    public long Version { get; set; }
}

/// <summary>Wire shape of a successful POST /api/logic/relays.</summary>
internal sealed class HmiLogicRelaySaveResponse
{
    public List<LogicRelayDto>? Relays { get; set; }

    public long Version { get; set; }
}

public sealed class BridgeApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private HttpClient client_ = new();

    public void SetBaseAddress(string baseUrl)
    {
        client_.Dispose();
        // Retire pooled sockets and bound every request: the post-reconnect snapshot refresh
        // must fail fast and retry rather than ride a connection the peer has already dropped.
        client_ = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<HmiTagsResponse> GetTagsAsync(CancellationToken ct)
    {
        HmiTagsResponse? response = await client_.GetFromJsonAsync<HmiTagsResponse>("api/hmi/tags", JsonOptions, ct)
            .ConfigureAwait(false);
        return response ?? new HmiTagsResponse();
    }

    public async Task<HmiWriteResponse> WriteAsync(HmiWriteRequest request, CancellationToken ct)
    {
        using HttpResponseMessage http = await client_.PostAsJsonAsync("api/hmi/write", request, JsonOptions, ct)
            .ConfigureAwait(false);
        HmiWriteResponse? body = await http.Content.ReadFromJsonAsync<HmiWriteResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        return body ?? new HmiWriteResponse { Ok = false, Error = $"HTTP {(int)http.StatusCode}" };
    }

    public async Task<HmiPortInfo?> GetPortsAsync(CancellationToken ct)
    {
        try
        {
            HmiPortInfo? info = await client_.GetFromJsonAsync<HmiPortInfo>(
                "api/status/ports", JsonOptions, ct).ConfigureAwait(false);
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Queries the bridge's live InfluxDB writer state. Returns null when the endpoint
    /// is unavailable (older bridge) so callers can fall back to per-tag history gating.
    /// </summary>
    public async Task<HmiInfluxStatus?> GetInfluxStatusAsync(CancellationToken ct)
    {
        try
        {
            return await client_.GetFromJsonAsync<HmiInfluxStatus>(
                "api/influx/status", JsonOptions, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<HmiTrendResponse> GetTrendsAsync(
        string sourceId,
        string itemId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int? maxPoints,
        CancellationToken ct)
    {
        var query = new List<string>
        {
            $"sourceId={Uri.EscapeDataString(sourceId)}",
            $"itemId={Uri.EscapeDataString(itemId)}"
        };
        if (fromUtc is not null)
        {
            query.Add($"from={Uri.EscapeDataString(fromUtc.Value.ToUniversalTime().ToString("o"))}");
        }
        if (toUtc is not null)
        {
            query.Add($"to={Uri.EscapeDataString(toUtc.Value.ToUniversalTime().ToString("o"))}");
        }
        if (maxPoints is not null)
        {
            query.Add($"maxPoints={maxPoints.Value}");
        }

        string path = "api/hmi/trends?" + string.Join("&", query);
        HmiTrendResponse? response = await client_.GetFromJsonAsync<HmiTrendResponse>(path, JsonOptions, ct)
            .ConfigureAwait(false);
        return response ?? new HmiTrendResponse
        {
            SourceId = sourceId,
            ItemId = itemId,
            Error = "Empty trends response"
        };
    }

    /// <summary>Plant-logic definitions and their version, from GET /api/logic.</summary>
    public async Task<HmiLogicDefinitions> GetLogicDefinitionsAsync(CancellationToken ct)
    {
        HmiLogicDefinitions? response = await client_.GetFromJsonAsync<HmiLogicDefinitions>("api/logic", JsonOptions, ct)
            .ConfigureAwait(false);
        return response ?? new HmiLogicDefinitions(
            Array.Empty<LogicBlockDto>(),
            Array.Empty<LogicRelayDto>(),
            0);
    }

    /// <summary>Evaluated logic state, from GET /api/logic/state.</summary>
    public async Task<LogicStateSnapshot> GetLogicStateAsync(CancellationToken ct)
    {
        LogicStateSnapshot? response = await client_.GetFromJsonAsync<LogicStateSnapshot>(
            "api/logic/state", JsonOptions, ct).ConfigureAwait(false);
        return response ?? new LogicStateSnapshot();
    }

    /// <summary>Saves one logic block; a rejected save answers with the bridge's own error text.</summary>
    public async Task<HmiLogicSaveResult> SaveLogicBlockAsync(LogicBlockDto block, CancellationToken ct)
    {
        using HttpResponseMessage http = await client_.PostAsJsonAsync("api/logic/blocks", new { block }, JsonOptions, ct)
            .ConfigureAwait(false);
        if (!http.IsSuccessStatusCode)
        {
            return new HmiLogicSaveResult(false, null, 0, await ReadLogicErrorAsync(http, ct).ConfigureAwait(false));
        }

        HmiLogicSaveResponse? body = await http.Content.ReadFromJsonAsync<HmiLogicSaveResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        return new HmiLogicSaveResult(true, body?.Block ?? block, body?.Version ?? 0, null);
    }

    /// <summary>
    /// Saves the relay/tag bindings; a rejected save answers with the bridge's own error text.
    /// The whole table is replaced, so the caller sends every binding it knows.
    /// </summary>
    public async Task<HmiLogicRelaySaveResult> SaveLogicRelaysAsync(
        IReadOnlyList<LogicRelayDto> relays,
        CancellationToken ct)
    {
        using HttpResponseMessage http = await client_
            .PostAsJsonAsync("api/logic/relays", new { relays }, JsonOptions, ct)
            .ConfigureAwait(false);
        if (!http.IsSuccessStatusCode)
        {
            return new HmiLogicRelaySaveResult(false, null, 0, await ReadLogicErrorAsync(http, ct).ConfigureAwait(false));
        }

        HmiLogicRelaySaveResponse? body = await http.Content.ReadFromJsonAsync<HmiLogicRelaySaveResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        return new HmiLogicRelaySaveResult(true, body?.Relays ?? relays, body?.Version ?? 0, null);
    }

    /// <summary>Deletes one logic block; a rejected delete answers with the bridge's own error text.</summary>
    public async Task<HmiLogicSaveResult> DeleteLogicBlockAsync(Guid id, CancellationToken ct)
    {
        using HttpResponseMessage http = await client_.DeleteAsync($"api/logic/blocks/{id:D}", ct).ConfigureAwait(false);
        if (!http.IsSuccessStatusCode)
        {
            return new HmiLogicSaveResult(false, null, 0, await ReadLogicErrorAsync(http, ct).ConfigureAwait(false));
        }

        HmiLogicVersionResponse? body = await http.Content.ReadFromJsonAsync<HmiLogicVersionResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        return new HmiLogicSaveResult(true, null, body?.Version ?? 0, null);
    }

    /// <summary>The bridge's <c>{error}</c> body when there is one, else the bare HTTP status.</summary>
    private static async Task<string> ReadLogicErrorAsync(HttpResponseMessage http, CancellationToken ct)
    {
        try
        {
            JsonElement? body = await http.Content.ReadFromJsonAsync<JsonElement?>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (body is { } element
                && element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString() ?? $"HTTP {(int)http.StatusCode}";
            }
        }
        catch
        {
            // malformed body: fall through to the status code
        }

        return $"HTTP {(int)http.StatusCode}";
    }

    public void Dispose() => client_.Dispose();
}
