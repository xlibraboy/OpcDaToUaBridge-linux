using System.Text.Json;
using System.Text.Json.Nodes;
using OpcBridge.Core;

namespace OpcBridge.Mqtt;

public static class MqttPayload
{
    public static string BuildTopic(MqttBrokerOptions options, string sourceId, string itemId, string? overrideTopic)
    {
        if (!string.IsNullOrWhiteSpace(overrideTopic))
        {
            return overrideTopic!.Trim();
        }

        string prefix = string.IsNullOrWhiteSpace(options.TopicPrefix) ? "bridge/tags" : options.TopicPrefix.Trim().Trim('/');
        return $"{prefix}/{sourceId.Trim()}/{itemId.Trim()}";
    }

    public static string Serialize(BridgeValue value, MqttPayloadField fields, string? displayName = null)
    {
        JsonObject obj = new();

        if (fields.HasFlag(MqttPayloadField.Value))
        {
            obj["v"] = JsonSerializer.SerializeToNode(value.Value);
        }

        if (fields.HasFlag(MqttPayloadField.Timestamp))
        {
            obj["t"] = value.TimestampUtc.ToString("o");
        }

        if (fields.HasFlag(MqttPayloadField.Quality))
        {
            obj["q"] = value.IsGood ? "Good" : "Bad";
        }

        if (fields.HasFlag(MqttPayloadField.SourceId))
        {
            obj["sourceId"] = value.SourceId;
        }

        if (fields.HasFlag(MqttPayloadField.ItemId))
        {
            obj["itemId"] = value.ItemId;
        }

        if (fields.HasFlag(MqttPayloadField.DisplayName))
        {
            obj["displayName"] = displayName ?? value.SourceId;
        }

        if (fields.HasFlag(MqttPayloadField.DataType))
        {
            obj["dataType"] = value.Value?.GetType().Name ?? "null";
        }

        return obj.ToJsonString();
    }

    /// <summary>Parse an inbound payload. Returns the raw "v" string (or whole payload), and a timestamp if "t" is present.</summary>
    public static (string? RawValue, DateTime? TimestampUtc) Parse(string payload)
    {
        ReadOnlySpan<char> span = payload.AsSpan().Trim();
        if (span.Length == 0)
        {
            return (null, null);
        }

        // Try JSON object with "v"/"t".
        try
        {
            using JsonDocument doc = JsonDocument.Parse(span.ToString());
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (!doc.RootElement.TryGetProperty("v", out JsonElement v))
                {
                    return (null, null);
                }
                string? raw = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
                DateTime? ts = null;
                if (doc.RootElement.TryGetProperty("t", out JsonElement t) && t.ValueKind == JsonValueKind.String)
                {
                    if (DateTime.TryParse(t.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
                    {
                        ts = parsed;
                    }
                }

                return (raw, ts);
            }
        }
        catch (JsonException)
        {
            // Not JSON — treat whole payload as the value.
        }

        return (span.ToString(), null);
    }
}
