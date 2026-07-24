using System.Net.Http.Json;
using System.Text.Json;
using OpcBridge.Client;

namespace OpcBridge.Hmi.Services;

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
        client_ = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
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

    public async Task<HmiTrendResponse> GetTrendsAsync(
        string sourceId,
        string daItemId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int? maxPoints,
        CancellationToken ct)
    {
        var query = new List<string>
        {
            $"sourceId={Uri.EscapeDataString(sourceId)}",
            $"daItemId={Uri.EscapeDataString(daItemId)}"
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
            DaItemId = daItemId,
            Error = "Empty trends response"
        };
    }

    public void Dispose() => client_.Dispose();
}
