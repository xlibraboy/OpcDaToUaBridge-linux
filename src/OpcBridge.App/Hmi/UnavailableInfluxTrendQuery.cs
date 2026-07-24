using OpcBridge.Client;

namespace OpcBridge.App.Hmi;

public sealed class UnavailableInfluxTrendQuery : IInfluxTrendQuery
{
    public Task<HmiTrendResponse> QueryAsync(
        string sourceId,
        string daItemId,
        DateTime fromUtc,
        DateTime toUtc,
        int maxPoints,
        CancellationToken ct)
    {
        return Task.FromResult(new HmiTrendResponse
        {
            SourceId = sourceId,
            DaItemId = daItemId,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Points = Array.Empty<HmiTrendPoint>(),
            Truncated = false,
            Error = "Influx not available"
        });
    }
}
