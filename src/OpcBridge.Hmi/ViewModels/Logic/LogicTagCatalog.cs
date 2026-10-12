using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>One tag choice in the logic editor: the identity the DTO stores plus the label it shows.</summary>
public sealed record LogicTagOption(string SourceId, string ItemId, string DisplayName, string SourceName, bool Writeable)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? ItemId : DisplayName;

    /// <summary>Full identity for tooltips: display name and source name.</summary>
    public string Detail => string.IsNullOrWhiteSpace(SourceName) ? Label : $"{Label} · {SourceName}";
}

/// <summary>One source choice: the id the DTO stores plus its configured display name.</summary>
public sealed record LogicSourceOption(string SourceId, string Name)
{
    public string Label => string.IsNullOrWhiteSpace(Name) ? SourceId : Name;

    /// <summary>Full identity for the pickers' accessible description: name and source id.</summary>
    public string Detail => string.IsNullOrWhiteSpace(SourceId)
        || string.Equals(SourceId, Label, StringComparison.OrdinalIgnoreCase)
            ? Label
            : $"{Label} · {SourceId}";
}

/// <summary>
/// Metadata-only snapshot of one bridge's tags for the editor's source/tag pickers (and the
/// result rows' tag labels). Taken when a block loads, so live value traffic and cache rebuilds
/// cannot disturb a selection in progress.
/// </summary>
public sealed class LogicTagCatalog
{
    public static LogicTagCatalog Empty { get; } = new(Array.Empty<LogicSourceOption>(), Array.Empty<LogicTagOption>());

    private readonly IReadOnlyList<LogicTagOption> tags_;

    public LogicTagCatalog(IReadOnlyList<LogicSourceOption> sources, IReadOnlyList<LogicTagOption> tags)
    {
        Sources = sources;
        tags_ = tags;
    }

    public IReadOnlyList<LogicSourceOption> Sources { get; }

    public static LogicTagCatalog Build(IEnumerable<MultiBridgeTagEntry> entries)
    {
        List<MultiBridgeTagEntry> ordered = entries
            .OrderBy(entry => entry.Key.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Key.DaItemId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sources = new List<LogicSourceOption>();
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<LogicTagOption>();
        foreach (MultiBridgeTagEntry entry in ordered)
        {
            if (seenSources.Add(entry.Key.SourceId))
            {
                sources.Add(new LogicSourceOption(entry.Key.SourceId, entry.SourceName));
            }

            tags.Add(new LogicTagOption(
                entry.Key.SourceId,
                entry.Key.DaItemId,
                entry.DisplayName,
                entry.SourceName,
                entry.Writeable));
        }

        return new LogicTagCatalog(sources, tags);
    }

    /// <summary>The tags of one source, in display order. A write-only picker lists writeable tags only.</summary>
    public IReadOnlyList<LogicTagOption> ItemsFor(string sourceId, bool writeableOnly = false)
    {
        List<LogicTagOption> items = new();
        foreach (LogicTagOption tag in tags_)
        {
            if (!string.Equals(tag.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (writeableOnly && !tag.Writeable)
            {
                continue;
            }

            items.Add(tag);
        }

        return items;
    }

    /// <summary>Display name and source name for a stored (sourceId, itemId) pair, else the item id.</summary>
    public string TagLabel(string sourceId, string itemId)
    {
        foreach (LogicTagOption tag in tags_)
        {
            if (string.Equals(tag.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(tag.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
            {
                return tag.Detail;
            }
        }

        return itemId;
    }
}
