namespace OpcBridge.App;

public sealed record MappingTagDto(
    string SourceId,
    string DaItemId,
    string? DisplayName = null,
    string? Description = null,
    string? DataType = null,
    string? UaNodeId = null,
    bool? Enabled = null,
    string? Mode = null,
    string? ManualValue = null,
    int? PollRateMs = null,
    float? DeadbandPct = null,
    bool? Writeable = null,
    string? AccessRights = null,
    bool? MqttEnabled = null,
    string? MqttTopic = null,
    bool? InfluxEnabled = null);

public sealed record MappingAddRequest(List<MappingTagDto>? Tags);

public sealed record MappingRemoveRequest(string SourceId, string DaItemId);

public sealed record MappingUpdateRequest(MappingTagDto Tag);