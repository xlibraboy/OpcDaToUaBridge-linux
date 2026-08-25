using System.Text.Json;

namespace OpcBridge.Client;

public sealed class DisplayDocumentDto
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public List<DisplayWidgetDto> Widgets { get; set; } = new();
}

public sealed class DisplayWidgetDto
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public int Z { get; set; }
    public Dictionary<string, JsonElement> Props { get; set; } = new(StringComparer.Ordinal);
    public TagBindingDto? Binding { get; set; }
}

public sealed class TagBindingDto
{
    public string BridgeId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string DaItemId { get; set; } = string.Empty;
}

public sealed class DisplayListItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public int WidgetCount { get; set; }
}

public sealed class DisplayListResponse
{
    public IReadOnlyList<DisplayListItemDto> Items { get; set; } = Array.Empty<DisplayListItemDto>();
}

public sealed class DisplayConflictResponse
{
    public string Error { get; set; } = "version conflict";
    public int CurrentVersion { get; set; }
}
