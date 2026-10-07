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
    string? DaGroup = null,
    float? DeadbandPct = null,
    bool? Writeable = null,
    string? AccessRights = null,
    bool? MqttEnabled = null,
    string? MqttTopic = null,
    bool? InfluxEnabled = null,
    string? Unit = null,
    double? RangeMin = null,
    double? RangeMax = null,
    string? Subscription = null,
    string? TrendStyle = null,
    bool? Digital = null,
    string? OnText = null,
    string? OffText = null,
    DateTime? AddedUtc = null);

public sealed record MappingAddRequest(List<MappingTagDto>? Tags);

public sealed record MappingRemoveRequest(string SourceId, string ItemId);

public sealed record MappingUpdateRequest(MappingTagDto Tag);

/// <summary>
/// A tag list to compare with a source before anything is added: the raw text of the file the
/// operator picked, exactly as read (the bridge parses it, so every entry point agrees on what
/// the file means), plus the name template the dialog is showing (#40) — the bridge renders each
/// row's name with it (<see cref="TagNameTemplate"/>), so the preview and the Add that follows
/// cannot disagree. A null or blank template means the default, the item id.
/// </summary>
public sealed record MappingImportPreviewRequest(string SourceId, string Text, string? NameTemplate = null);
