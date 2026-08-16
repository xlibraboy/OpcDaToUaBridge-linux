namespace OpcBridge.Client;

public interface IInfluxTrendQuery
{
    Task<HmiTrendResponse> QueryAsync(
        string sourceId,
        string itemId,
        DateTime fromUtc,
        DateTime toUtc,
        int maxPoints,
        CancellationToken ct);
}
