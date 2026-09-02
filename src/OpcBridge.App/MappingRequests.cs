namespace OpcBridge.App;

public sealed record MappingTagDto(
    string SourceId,
    string ItemId,
    string? DisplayName = null,
    string? Description = null,
    string? DataType = null,
    string? UaNodeId = null,
    bool? Enabled = null,
    string? Mode = null,
    string? ManualValue = null,
    int? PollRateMs = null,
    int? Decimals = null,
    float? DeadbandPct = null,
    bool? Writeable = null,
    string? AccessRights = null,
    bool? MqttEnabled = null,
    string? MqttTopic = null,
    bool? InfluxEnabled = null,
    string? Unit = null,
    string? Subscription = null,
    string? PlcGroup = null);

public sealed record MappingAddRequest(List<MappingTagDto>? Tags);

public sealed record MappingRemoveRequest(string SourceId, string ItemId);

public sealed record MappingUpdateRequest(MappingTagDto Tag);
