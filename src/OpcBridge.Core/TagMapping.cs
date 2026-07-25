namespace OpcBridge.Core;

public sealed class TagMapping
{
    /// <summary>
    /// When set, this tag is "fed" by another tag: the provider tag's value is forwarded
    /// as a write into this tag's DA item. Direction/permission is governed by the
    /// provider's AccessRights (must allow Read) and this tag's AccessRights (must allow Write).
    /// Optional — a tag with no provider is a normal standalone mapping.
    /// </summary>
    public string? ProviderSourceId { get; set; }
    public string? ProviderDaItemId { get; set; }

    public string SourceId { get; set; } = "default";
    public string DaItemId { get; set; } = string.Empty;
    public string UaNodeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DataType { get; set; } = "Double";
    public bool Enabled { get; set; } = true;
    public string Mode { get; set; } = TagMode.Source;
    public string? ManualValue { get; set; }
    public int PollRateMs { get; set; }
    public float DeadbandPct { get; set; }
    public bool Writeable { get; set; }
    public string AccessRights { get; set; } = TagAccessRights.Read;
    public bool MqttEnabled { get; set; }
    public string? MqttTopic { get; set; }
    public bool InfluxEnabled { get; set; }
}
public static class TagMode
{
    public const string Source = "Source";
    public const string Manual = "Manual";
}

public static class TagAccessRights
{
    public const string Read = "Read";
    public const string ReadWrite = "Read-Write";
    public const string Write = "Write";
}
