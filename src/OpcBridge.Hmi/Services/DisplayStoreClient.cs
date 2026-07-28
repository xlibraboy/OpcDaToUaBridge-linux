using System.Net.Http.Json;
using System.Text.Json;
using OpcBridge.Client;

namespace OpcBridge.Hmi.Services;

public sealed class DisplayStoreClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private HttpClient client_ = new();

    public void SetBaseAddress(string baseUrl)
    {
        client_.Dispose();
        client_ = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
    }

    public async Task<DisplayListResponse> ListAsync(CancellationToken ct)
    {
        DisplayListResponse? response = await client_
            .GetFromJsonAsync<DisplayListResponse>("api/hmi/displays", JsonOptions, ct)
            .ConfigureAwait(false);
        return response ?? new DisplayListResponse();
    }

    public async Task<DisplayDocumentDto?> GetAsync(string id, CancellationToken ct)
    {
        using HttpResponseMessage http = await client_
            .GetAsync("api/hmi/displays/" + Uri.EscapeDataString(id), ct)
            .ConfigureAwait(false);
        if (http.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        http.EnsureSuccessStatusCode();
        return await http.Content.ReadFromJsonAsync<DisplayDocumentDto>(JsonOptions, ct).ConfigureAwait(false);
    }

    public async Task<(DisplayDocumentDto? Document, int StatusCode, string? Error, int? CurrentVersion)> PutAsync(
        string id,
        DisplayDocumentDto document,
        CancellationToken ct)
    {
        document.Id = id;
        using HttpResponseMessage http = await client_
            .PutAsJsonAsync("api/hmi/displays/" + Uri.EscapeDataString(id), document, JsonOptions, ct)
            .ConfigureAwait(false);
        string body = await http.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (http.IsSuccessStatusCode)
        {
            DisplayDocumentDto? doc = JsonSerializer.Deserialize<DisplayDocumentDto>(body, JsonOptions);
            return (doc, (int)http.StatusCode, null, doc?.Version);
        }

        if (http.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            try
            {
                DisplayConflictResponse? conflict = JsonSerializer.Deserialize<DisplayConflictResponse>(body, JsonOptions);
                return (null, 409, conflict?.Error ?? "version conflict", conflict?.CurrentVersion);
            }
            catch
            {
                return (null, 409, "version conflict", null);
            }
        }

        try
        {
            using JsonDocument err = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            string? error = err.RootElement.TryGetProperty("error", out JsonElement e) ? e.GetString() : body;
            return (null, (int)http.StatusCode, error, null);
        }
        catch
        {
            return (null, (int)http.StatusCode, body, null);
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        using HttpResponseMessage http = await client_
            .DeleteAsync("api/hmi/displays/" + Uri.EscapeDataString(id), ct)
            .ConfigureAwait(false);
        if (http.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        http.EnsureSuccessStatusCode();
        return true;
    }

    public void Dispose() => client_.Dispose();
}
