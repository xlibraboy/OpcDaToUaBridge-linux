using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpcBridge.Hmi.Core;

/// <summary>
/// Persistence for saved trend groups, next to the HMI client config. Loading never throws:
/// a missing or corrupt file reads as "no groups yet" so the operator app still starts.
/// </summary>
public static class TrendGroupStore
{
    public static string DefaultPath(string configPath) =>
        Path.Combine(
            Path.GetDirectoryName(configPath) ?? ".",
            "hmi-trendgroups.json");

    public static List<TrendGroupDefinition> Load(string path)
    {
        var groups = new List<TrendGroupDefinition>();
        if (!File.Exists(path))
        {
            return groups;
        }

        try
        {
            string json = File.ReadAllText(path);
            groups = JsonSerializer.Deserialize<List<TrendGroupDefinition>>(json, JsonOptions) ?? new List<TrendGroupDefinition>();
        }
        catch
        {
            // corrupt file: start from an empty list rather than failing the app
            return new List<TrendGroupDefinition>();
        }

        Normalize(groups);
        return groups;
    }

    public static void Save(string path, IEnumerable<TrendGroupDefinition> groups)
    {
        var snapshot = groups.ToList();
        Normalize(snapshot);

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    /// <summary>Trims and repairs a group list: stable unique ids, non-blank names, usable pens.</summary>
    public static void Normalize(List<TrendGroupDefinition> groups)
    {
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < groups.Count; i++)
        {
            TrendGroupDefinition group = groups[i];
            group.Name = (group.Name ?? string.Empty).Trim();
            if (group.Name.Length == 0)
            {
                group.Name = $"Trend group {i + 1}";
            }

            group.Id = (group.Id ?? string.Empty).Trim();
            if (group.Id.Length == 0 || !usedIds.Add(group.Id))
            {
                group.Id = Guid.NewGuid().ToString("N");
                usedIds.Add(group.Id);
            }

            group.LayoutMode = TrendGroupLayouts.Normalize(group.LayoutMode);
            group.YAxisMode = TrendGroupAxisModes.Normalize(group.YAxisMode);
            NormalizePens(group);
        }
    }

    private static void NormalizePens(TrendGroupDefinition group)
    {
        var pens = new List<TrendGroupPenDefinition>(group.Pens.Count);
        var usedKeys = new HashSet<TagBindingKey>(TagBindingKeyComparer.Instance);
        foreach (TrendGroupPenDefinition pen in group.Pens)
        {
            pen.BridgeId = (pen.BridgeId ?? string.Empty).Trim();
            pen.SourceId = (pen.SourceId ?? string.Empty).Trim();
            pen.DaItemId = (pen.DaItemId ?? string.Empty).Trim();
            // A pen with any blank key part cannot be resolved to a tag, and a repeated tag would
            // plot twice — both are dropped rather than kept as broken rows.
            if (pen.BridgeId.Length == 0
                || pen.SourceId.Length == 0
                || pen.DaItemId.Length == 0
                || !usedKeys.Add(pen.Key))
            {
                continue;
            }

            pen.Color = NormalizeColor(pen.Color);
            pen.RangeMin = NormalizeRange(pen.RangeMin);
            pen.RangeMax = NormalizeRange(pen.RangeMax);
            pens.Add(pen);
        }

        group.Pens = pens;
    }

    private static string NormalizeColor(string? color)
    {
        string value = (color ?? string.Empty).Trim();
        if (value.Length != 7 || value[0] != '#')
        {
            return string.Empty;
        }

        for (int i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
            {
                return string.Empty;
            }
        }

        return value.ToUpperInvariant();
    }

    private static double? NormalizeRange(double? value) =>
        value is { } v && double.IsFinite(v) ? v : null;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
}
