using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpcBridge.Client;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// HTTP client for the bridge's mobile-facing surface: logic definitions and state, the tag
/// snapshot, the existing HMI write path, operator notes and sign-in. The cookie container
/// carries the bridge session like the HMI designer does; with the default
/// <c>Auth:TrustHmi</c> the logic endpoints need no sign-in at all.
/// </summary>
public sealed class LogicApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CookieContainer cookies_ = new();
    private HttpClient client_ = new();
    private string base_url_ = string.Empty;

    public string BaseUrl => base_url_;

    public void SetBaseAddress(string baseUrl)
    {
        client_.Dispose();
        // Retire pooled sockets and bound every request: after a Wi-Fi drop or a bridge
        // restart the phone must fail fast and retry rather than ride a dead connection.
        client_ = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            CookieContainer = cookies_,
            UseCookies = true
        })
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        base_url_ = baseUrl.TrimEnd('/');
    }

    public Task<MobileResult<LogicStateSnapshot>> GetLogicStateAsync(CancellationToken cancellationToken) =>
        GetJsonAsync<LogicStateSnapshot>("api/logic/state", cancellationToken);

    public async Task<MobileResult<IReadOnlyList<LogicBlockDto>>> GetLogicBlocksAsync(CancellationToken cancellationToken)
    {
        MobileResult<LogicBlocksResponse> result = await GetJsonAsync<LogicBlocksResponse>("api/logic", cancellationToken)
            .ConfigureAwait(false);
        return result.Ok
            ? MobileResult<IReadOnlyList<LogicBlockDto>>.Success(result.Value?.Blocks ?? new List<LogicBlockDto>())
            : MobileResult<IReadOnlyList<LogicBlockDto>>.Fail(result.Error ?? "request failed");
    }

    public async Task<MobileResult<IReadOnlyList<HmiTagDto>>> GetTagsAsync(CancellationToken cancellationToken)
    {
        MobileResult<HmiTagsResponse> result = await GetJsonAsync<HmiTagsResponse>("api/hmi/tags", cancellationToken)
            .ConfigureAwait(false);
        return result.Ok
            ? MobileResult<IReadOnlyList<HmiTagDto>>.Success(result.Value?.Tags ?? new List<HmiTagDto>())
            : MobileResult<IReadOnlyList<HmiTagDto>>.Fail(result.Error ?? "request failed");
    }

    public async Task<MobileResult<IReadOnlyList<LogicNoteDto>>> GetNotesAsync(Guid? blockId, int limit, CancellationToken cancellationToken)
    {
        string query = blockId is null ? $"?limit={limit}" : $"?blockId={blockId}&limit={limit}";
        MobileResult<LogicNotesResponse> result = await GetJsonAsync<LogicNotesResponse>($"api/logic/notes{query}", cancellationToken)
            .ConfigureAwait(false);
        return result.Ok
            ? MobileResult<IReadOnlyList<LogicNoteDto>>.Success(result.Value?.Notes ?? new List<LogicNoteDto>())
            : MobileResult<IReadOnlyList<LogicNoteDto>>.Fail(result.Error ?? "request failed");
    }

    public async Task<MobileResult<LogicNoteDto>> AddNoteAsync(LogicNoteAddRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await client_
                .PostAsJsonAsync("api/logic/notes", request, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return MobileResult<LogicNoteDto>.Fail(ReadError(body) ?? $"HTTP {(int)response.StatusCode}");
            }

            LogicNoteResponse? parsed = JsonSerializer.Deserialize<LogicNoteResponse>(body, JsonOptions);
            return parsed?.Note is null
                ? MobileResult<LogicNoteDto>.Fail("empty response")
                : MobileResult<LogicNoteDto>.Success(parsed.Note);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            return MobileResult<LogicNoteDto>.Fail(ex.Message);
        }
    }

    public async Task<HmiWriteResponse> WriteAsync(HmiWriteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await client_
                .PostAsJsonAsync("api/hmi/write", request, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new HmiWriteResponse { Ok = false, Error = ReadError(body) ?? $"HTTP {(int)response.StatusCode}" };
            }

            return JsonSerializer.Deserialize<HmiWriteResponse>(body, JsonOptions)
                ?? new HmiWriteResponse { Ok = false, Error = "empty response" };
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            return new HmiWriteResponse { Ok = false, Error = ex.Message };
        }
    }

    public Task<MobileResult<SessionResponse>> GetSessionAsync(CancellationToken cancellationToken) =>
        GetJsonAsync<SessionResponse>("api/auth/me", cancellationToken);

    public async Task<MobileResult<SessionResponse>> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await client_
                .PostAsJsonAsync("api/auth/login", new { username, password }, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return MobileResult<SessionResponse>.Fail(ReadError(body) ?? $"HTTP {(int)response.StatusCode}");
            }

            SessionResponse? parsed = JsonSerializer.Deserialize<SessionResponse>(body, JsonOptions);
            return parsed is null
                ? MobileResult<SessionResponse>.Fail("empty response")
                : MobileResult<SessionResponse>.Success(parsed);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            return MobileResult<SessionResponse>.Fail(ex.Message);
        }
    }

    public void Dispose() => client_.Dispose();

    private async Task<MobileResult<T>> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await client_.GetAsync(path, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return MobileResult<T>.Fail(ReadError(body) ?? $"HTTP {(int)response.StatusCode}");
            }

            T? value = JsonSerializer.Deserialize<T>(body, JsonOptions);
            return value is null ? MobileResult<T>.Fail("empty response") : MobileResult<T>.Success(value);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            return MobileResult<T>.Fail(ex.Message);
        }
    }

    private static bool IsTransport(Exception exception, CancellationToken cancellationToken)
    {
        // A cancellation the caller asked for must propagate; everything else is a failed call.
        return exception switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested => false,
            HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException => true,
            _ => false
        };
    }

    private static string? ReadError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private sealed class LogicBlocksResponse
    {
        public List<LogicBlockDto> Blocks { get; set; } = new();
    }

    private sealed class HmiTagsResponse
    {
        public List<HmiTagDto> Tags { get; set; } = new();
    }

    private sealed class LogicNotesResponse
    {
        public List<LogicNoteDto> Notes { get; set; } = new();
    }

    private sealed class LogicNoteResponse
    {
        public LogicNoteDto? Note { get; set; }
    }

    /// <summary>/api/auth/me and /api/auth/login response shape (only what the phone needs).</summary>
    public sealed class SessionResponse
    {
        public bool Authenticated { get; set; }
        public string Username { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Role { get; set; } = "Viewer";
        public bool AuthEnabled { get; set; }
    }
}
