using System.Text.Json.Serialization;
namespace OpcBridge.Core;

public sealed record BridgeValue(
    string SourceId,
    [property: JsonPropertyName("itemId")] string ItemId,
    object? Value,
    DateTime TimestampUtc,
    int DaQuality,
    bool IsGood);