using OpcBridge.Client;

namespace OpcBridge.App.Hmi;

public interface IInfluxTrendQuery
{
    Task<HmiTrendResponse> QueryAsync(
        string sourceId,
        string daItemId,
        DateTime fromUtc,
        DateTime toUtc,
        int maxPoints,
        CancellationToken ct);
}
