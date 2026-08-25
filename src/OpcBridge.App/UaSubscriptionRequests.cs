namespace OpcBridge.App;

public sealed record UaSubscriptionUpsertRequest(string SourceId, string Name, int UpdateRateMs);

public sealed record UaSubscriptionRemoveRequest(string SourceId, string Name);
