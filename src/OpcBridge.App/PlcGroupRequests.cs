namespace OpcBridge.App;

public sealed record PlcGroupUpsertRequest(string SourceId, string Name, int UpdateRateMs);

public sealed record PlcGroupRemoveRequest(string SourceId, string Name);
