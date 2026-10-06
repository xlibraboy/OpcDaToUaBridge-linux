using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;

namespace OpcBridge.Hmi.Designer.Services;

/// <summary>What the bridge reports about its authentication state (GET /api/auth/me).</summary>
public sealed record BridgeAuthInfo(
    bool AuthEnabled,
    bool Authenticated,
    string? Username,
    string? DisplayName,
    string? Role);

/// <summary>One OPC source configured on the bridge, as shown in the Designer's source list.</summary>
public sealed record BridgeSourceInfo(
    string SourceId,
    string DisplayName,
    string SourceType,
    string Endpoint,
    string? ConnectionState)
{
    public string TypeLabel => SourceTypeLabels.ShortLabel(SourceType);

    public bool HasTypeLabel => TypeLabel.Length > 0;

    public string DisplayNameOrId => string.IsNullOrWhiteSpace(DisplayName) ? SourceId : DisplayName.Trim();

    /// <summary>A one-line description: what the source talks to (server, endpoint or serial port).</summary>
    public string EndpointSummary => string.IsNullOrWhiteSpace(Endpoint) ? SourceId : Endpoint.Trim();

    public bool HasState => !string.IsNullOrWhiteSpace(ConnectionState);
}

/// <summary>Result of listing sources: an unreachable bridge, an auth wall, or the sources.</summary>
public sealed record SourceListResult(
    bool Ok,
    bool AuthRequired,
    string? Error,
    IReadOnlyList<BridgeSourceInfo> Sources)
{
    public static SourceListResult Failed(string error, bool authRequired = false) =>
        new(false, authRequired, error, Array.Empty<BridgeSourceInfo>());
}

/// <summary>
/// The Designer's bridge connection: authentication, the configured source list and local
/// bridge detection. One cookie-aware client, so a sign-in persists for every later call.
/// </summary>
public interface IDesignerBridgeClient : IDisposable
{
    void SetBaseAddress(string baseUrl);

    /// <summary>Probes for a locally running bridge; null when none answers.</summary>
    Task<string?> DetectAsync(CancellationToken ct);

    /// <summary>Auth state from /api/auth/me; null when the bridge cannot be reached.</summary>
    Task<BridgeAuthInfo?> GetAuthInfoAsync(CancellationToken ct);

    Task<(bool Ok, string? Error)> SignInAsync(string username, string password, CancellationToken ct);

    Task SignOutAsync(CancellationToken ct);

    /// <summary>Configured sources (/api/da/sources), with connection state merged from /api/status.</summary>
    Task<SourceListResult> GetSourcesAsync(CancellationToken ct);
}

public sealed class DesignerBridgeClient : IDesignerBridgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CookieContainer cookies_ = new();
    private HttpClient client_;

    public DesignerBridgeClient()
    {
        client_ = NewClient(string.Empty, cookies_);
    }

    public void SetBaseAddress(string baseUrl)
    {
        client_.Dispose();
        client_ = NewClient(baseUrl, cookies_);
    }

    public Task<string?> DetectAsync(CancellationToken ct) => LocalBridgeDetector.DetectAsync(ct: ct);

    public async Task<BridgeAuthInfo?> GetAuthInfoAsync(CancellationToken ct)
    {
        try
        {
            AuthMeResponse? me = await client_
                .GetFromJsonAsync<AuthMeResponse>("api/auth/me", JsonOptions, ct)
                .ConfigureAwait(false);
            return me is null
                ? null
                : new BridgeAuthInfo(me.AuthEnabled, me.Authenticated, me.Username, me.DisplayName, me.Role);
        }
        catch
        {
            return null;
        }
    }

    public async Task<(bool Ok, string? Error)> SignInAsync(string username, string password, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await client_
                .PostAsJsonAsync("api/auth/login", new { username, password }, JsonOptions, ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            string? error = await ReadErrorAsync(response, ct).ConfigureAwait(false);
            return (false, error ?? $"Sign-in failed (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception ex)
        {
            return (false, "Sign-in failed: " + ex.Message);
        }
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await client_.PostAsync("api/auth/logout", null, ct)
                .ConfigureAwait(false);
            _ = response;
        }
        catch
        {
            // Signing out of an unreachable bridge still clears the local UI state.
        }
    }

    public async Task<SourceListResult> GetSourcesAsync(CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await client_.GetAsync("api/da/sources", ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return SourceListResult.Failed("Sign in to load the full source list.", authRequired: true);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return SourceListResult.Failed("Signed in, but this account may not read sources.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return SourceListResult.Failed($"Source list failed (HTTP {(int)response.StatusCode}).");
            }

            SourceListResponse? body = await response.Content
                .ReadFromJsonAsync<SourceListResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
            List<BridgeSourceInfo> sources = (body?.Sources ?? new List<SourceApiDto>())
                .Select(ToSourceInfo)
                .ToList();

            IReadOnlyDictionary<string, string> states = await GetSourceStatesAsync(ct).ConfigureAwait(false);
            if (states.Count > 0)
            {
                sources = sources
                    .Select(source => states.TryGetValue(source.SourceId, out string? state)
                        ? source with { ConnectionState = state }
                        : source)
                    .ToList();
            }

            return new SourceListResult(true, false, null, sources);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return SourceListResult.Failed("Bridge not reachable: " + ex.Message);
        }
        catch (Exception ex)
        {
            return SourceListResult.Failed("Source list failed: " + ex.Message);
        }
    }

    /// <summary>Connection state per source from /api/status — best effort, empty when unavailable.</summary>
    private async Task<IReadOnlyDictionary<string, string>> GetSourceStatesAsync(CancellationToken ct)
    {
        try
        {
            StatusResponse? status = await client_
                .GetFromJsonAsync<StatusResponse>("api/status", JsonOptions, ct)
                .ConfigureAwait(false);
            return (status?.Bridge?.Sources ?? new List<StatusSourceDto>())
                .Where(source => !string.IsNullOrWhiteSpace(source.SourceId) && !string.IsNullOrWhiteSpace(source.ConnectionState))
                .GroupBy(source => source.SourceId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().ConnectionState!,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static BridgeSourceInfo ToSourceInfo(SourceApiDto source)
    {
        string endpoint = !string.IsNullOrWhiteSpace(source.EndpointUrl)
            ? source.EndpointUrl!
            : !string.IsNullOrWhiteSpace(source.ProgId)
                ? (string.IsNullOrWhiteSpace(source.Host) ? source.ProgId! : $"{source.ProgId} @ {source.Host}")
                : source.SerialPortName ?? string.Empty;

        return new BridgeSourceInfo(
            source.SourceId ?? string.Empty,
            source.DisplayName ?? string.Empty,
            source.SourceType ?? string.Empty,
            endpoint,
            null);
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            ErrorResponse? body = await response.Content
                .ReadFromJsonAsync<ErrorResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body?.Error) ? null : body!.Error;
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient NewClient(string baseUrl, CookieContainer cookies)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = true,
            CookieContainer = cookies,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };

        HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(15) };
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        }

        return client;
    }

    public void Dispose() => client_.Dispose();

    private sealed class AuthMeResponse
    {
        public bool Authenticated { get; set; }
        public bool AuthEnabled { get; set; }
        public string? Username { get; set; }
        public string? DisplayName { get; set; }
        public string? Role { get; set; }
    }

    private sealed class ErrorResponse
    {
        public string? Error { get; set; }
    }

    private sealed class SourceListResponse
    {
        public List<SourceApiDto>? Sources { get; set; }
    }

    private sealed class SourceApiDto
    {
        public string? SourceId { get; set; }
        public string? DisplayName { get; set; }
        public string? SourceType { get; set; }
        public string? ProgId { get; set; }
        public string? Host { get; set; }
        public string? EndpointUrl { get; set; }
        public string? SerialPortName { get; set; }
    }

    private sealed class StatusResponse
    {
        public StatusBridgeDto? Bridge { get; set; }
    }

    private sealed class StatusBridgeDto
    {
        public List<StatusSourceDto>? Sources { get; set; }
    }

    private sealed class StatusSourceDto
    {
        public string? SourceId { get; set; }
        public string? ConnectionState { get; set; }
    }
}
