using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using OpcBridge.Client;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// One bridge's logic surface: its blocks and the areas' tags the blocks' named tags resolve
/// through (a block's rows reference an entry by name; the pair lives on the area's list).
/// </summary>
public sealed record MobileLogicDefinitions(
    IReadOnlyList<LogicBlockDto> Blocks,
    IReadOnlyList<LogicAreaTagsDto> TagSources);

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

    private const string NotOnAccessPoint = AccessPointGate.BlockedText;

    private readonly CookieContainer cookies_ = new();
    private HttpClient client_ = new();
    private string base_url_ = string.Empty;

    public string BaseUrl => base_url_;

    /// <summary>
    /// Asked before every request. When it answers false (the phone is not on the bound plant
    /// access point), the call fails before a socket is opened instead of quietly riding
    /// another network.
    /// </summary>
    public Func<bool>? CanGoOnline { get; set; }

    /// <summary>
    /// Asked before sign-in on a phone that has no access point provisioned yet. This is the
    /// one deliberate exception to the online gate: an installer may sign in to an
    /// Engineer/Admin bridge account while standing on the plant network, so the same control
    /// users can provision the access point. No other call is allowed until the BSSID matches.
    /// </summary>
    public Func<bool>? CanAuthenticate { get; set; }

    private bool OnlineAllowed => CanGoOnline?.Invoke() ?? true;

    private bool AuthenticationAllowed => CanAuthenticate?.Invoke() ?? OnlineAllowed;

    /// <summary>
    /// The bridge's session cookie, shared with the hub link so a hardened bridge
    /// (Auth on, <c>TrustHmi</c> off) accepts the live connection after sign-in.
    /// </summary>
    internal CookieContainer Cookies => cookies_;

    public void SetBaseAddress(string baseUrl)
    {
        string normalized = (baseUrl ?? string.Empty).TrimEnd('/');
        if (string.Equals(base_url_, normalized, StringComparison.OrdinalIgnoreCase))
        {
            // A sign-in on an already-connected bridge must not replace the live client (and
            // its pooled connection) — the coordinator's ConnectAsync already set the address.
            return;
        }

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
            BaseAddress = new Uri(normalized + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        base_url_ = normalized;
    }

    public Task<MobileResult<LogicStateSnapshot>> GetLogicStateAsync(CancellationToken cancellationToken) =>
        GetJsonAsync<LogicStateSnapshot>("api/logic/state", cancellationToken);

    public async Task<MobileResult<MobileLogicDefinitions>> GetLogicDefinitionsAsync(CancellationToken cancellationToken)
    {
        MobileResult<LogicBlocksResponse> result = await GetJsonAsync<LogicBlocksResponse>("api/logic", cancellationToken)
            .ConfigureAwait(false);
        // Keep the status code: the caller turns a 401 into "the bridge requires sign-in".
        return result.Ok
            ? MobileResult<MobileLogicDefinitions>.Success(new MobileLogicDefinitions(
                result.Value?.Blocks ?? new List<LogicBlockDto>(),
                result.Value?.TagSources ?? new List<LogicAreaTagsDto>()))
            : MobileResult<MobileLogicDefinitions>.Fail(result.Error ?? "request failed", result.StatusCode);
    }

    public async Task<MobileResult<IReadOnlyList<HmiTagDto>>> GetTagsAsync(CancellationToken cancellationToken)
    {
        MobileResult<HmiTagsResponse> result = await GetJsonAsync<HmiTagsResponse>("api/hmi/tags", cancellationToken)
            .ConfigureAwait(false);
        return result.Ok
            ? MobileResult<IReadOnlyList<HmiTagDto>>.Success(result.Value?.Tags ?? new List<HmiTagDto>())
            : MobileResult<IReadOnlyList<HmiTagDto>>.Fail(result.Error ?? "request failed", result.StatusCode);
    }

    public async Task<MobileResult<IReadOnlyList<LogicNoteDto>>> GetNotesAsync(Guid? blockId, int limit, CancellationToken cancellationToken)
    {
        string query = blockId is null ? $"?limit={limit}" : $"?blockId={blockId}&limit={limit}";
        MobileResult<LogicNotesResponse> result = await GetJsonAsync<LogicNotesResponse>($"api/logic/notes{query}", cancellationToken)
            .ConfigureAwait(false);
        return result.Ok
            ? MobileResult<IReadOnlyList<LogicNoteDto>>.Success(result.Value?.Notes ?? new List<LogicNoteDto>())
            : MobileResult<IReadOnlyList<LogicNoteDto>>.Fail(result.Error ?? "request failed", result.StatusCode);
    }

    public async Task<MobileResult<LogicNoteDto>> AddNoteAsync(LogicNoteAddRequest request, CancellationToken cancellationToken)
    {
        if (!OnlineAllowed)
        {
            return MobileResult<LogicNoteDto>.Fail(NotOnAccessPoint);
        }

        try
        {
            using HttpResponseMessage response = await client_
                .PostAsJsonAsync("api/logic/notes", request, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return MobileResult<LogicNoteDto>.Fail(ReadError(body) ?? $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);
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

    public async Task<MobileResult<HmiWriteResponse>> WriteAsync(HmiWriteRequest request, CancellationToken cancellationToken)
    {
        if (!OnlineAllowed)
        {
            return MobileResult<HmiWriteResponse>.Fail(NotOnAccessPoint);
        }

        try
        {
            using HttpResponseMessage response = await client_
                .PostAsJsonAsync("api/hmi/write", request, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return MobileResult<HmiWriteResponse>.Fail(
                    ReadError(body) ?? $"HTTP {(int)response.StatusCode}",
                    (int)response.StatusCode);
            }

            HmiWriteResponse? parsed = JsonSerializer.Deserialize<HmiWriteResponse>(body, JsonOptions);
            if (parsed is null)
            {
                return MobileResult<HmiWriteResponse>.Fail("empty response");
            }

            // The bridge reports a refused write as HTTP 200 with { ok: false }.
            return new MobileResult<HmiWriteResponse>(parsed.Ok, parsed, parsed.Error, 200);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            return MobileResult<HmiWriteResponse>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Round-trip to the bridge's public <c>/health</c>. The stopwatch spans the request and the
    /// tiny response, so it measures the phone-to-bridge path rather than an endpoint's work.
    /// </summary>
    public async Task<MobileResult<int>> MeasureLatencyAsync(CancellationToken cancellationToken)
    {
        if (!OnlineAllowed)
        {
            return MobileResult<int>.Fail(NotOnAccessPoint);
        }

        if (string.IsNullOrWhiteSpace(base_url_))
        {
            return MobileResult<int>.Fail("no bridge address");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await client_
                .GetAsync("health", cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            return response.IsSuccessStatusCode
                ? MobileResult<int>.Success((int)stopwatch.ElapsedMilliseconds)
                : MobileResult<int>.Fail(
                    $"HTTP {(int)response.StatusCode}",
                    (int)response.StatusCode);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A ping must never surface an exception; a disposed client during a reconnect is
            // just "no measurement this round".
            return MobileResult<int>.Fail(ex.Message);
        }
    }

    public Task<MobileResult<SessionResponse>> GetSessionAsync(CancellationToken cancellationToken) =>
        GetJsonAsync<SessionResponse>("api/auth/me", cancellationToken);

    public async Task<MobileResult<SessionResponse>> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (!AuthenticationAllowed)
        {
            return MobileResult<SessionResponse>.Fail(NotOnAccessPoint);
        }

        try
        {
            using HttpResponseMessage response = await client_
                .PostAsJsonAsync("api/auth/login", new { username, password }, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return MobileResult<SessionResponse>.Fail(ReadError(body) ?? $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);
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

    /// <summary>
    /// Ends the bridge session. A successful response carries the expired session cookie; a
    /// failed call is ignored, because the app clears its own session either way.
    /// </summary>
    public async Task<MobileResult<bool>> LogoutAsync(CancellationToken cancellationToken)
    {
        if (!OnlineAllowed)
        {
            return MobileResult<bool>.Fail(NotOnAccessPoint);
        }

        try
        {
            using HttpResponseMessage response = await client_
                .PostAsync("api/auth/logout", null, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? MobileResult<bool>.Success(true)
                : MobileResult<bool>.Fail($"HTTP {(int)response.StatusCode}", (int)response.StatusCode);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            return MobileResult<bool>.Fail(ex.Message);
        }
    }

    public void Dispose() => client_.Dispose();

    private async Task<MobileResult<T>> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!OnlineAllowed)
        {
            return MobileResult<T>.Fail(NotOnAccessPoint);
        }

        try
        {
            using HttpResponseMessage response = await client_.GetAsync(path, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return MobileResult<T>.Fail(ReadError(body) ?? $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);
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
        // Android's HTTP handler reports a socket closed before any response as WebException,
        // where the desktop transport wraps it as HttpRequestException — both are transport
        // failures the page renders, never an exception that reaches the process.
        return exception switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested => false,
            HttpRequestException or WebException or IOException or SocketException
                or TaskCanceledException or OperationCanceledException or JsonException => true,
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

        /// <summary>The areas' tags a block's named tags resolve through.</summary>
        public List<LogicAreaTagsDto>? TagSources { get; set; }
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
