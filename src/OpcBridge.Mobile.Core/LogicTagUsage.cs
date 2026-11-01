using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// One tag a block row reads or writes: a name in the block's area tags, or a direct
/// (source, item) pair. Its identity is the mapped pair when the name is bound, else the name
/// inside its area — the same rule the web Result tab's tag inspector uses.
/// </summary>
public sealed class LogicTagReference
{
    public bool Named { get; init; }

    /// <summary>The area's entry name for a named reference; empty for a direct pair.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The block's area (the scope the name resolves in).</summary>
    public string Area { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    /// <summary>Identity used to join every reference to the same tag.</summary>
    public string Key { get; init; } = string.Empty;

    public bool IsBound => SourceId.Length > 0 && ItemId.Length > 0;

    internal static string PairKey(string sourceId, string itemId) =>
        "pair|" + sourceId.Trim().ToLowerInvariant() + "|" + itemId.Trim().ToLowerInvariant();

    internal static string NameKey(string area, string name) =>
        "name|" + area.Trim().ToLowerInvariant() + "|" + name.Trim().ToLowerInvariant();
}

/// <summary>
/// Builds a row's tag identity the way the web Result board does: a relay is internal and has
/// no source tag; a coil carries the block's output tag; a named entry resolves through the
/// block's area's tags; anything else is the row's direct (source, item) pair.
/// </summary>
public static class LogicTagReferences
{
    public static LogicTagReference? ForElement(
        LogicBlockDto block,
        LogicElementDto element,
        LogicAreaTagsDto? areaTags)
    {
        if (!string.IsNullOrWhiteSpace(element.Relay))
        {
            return null;
        }

        string named = (element.Tag ?? string.Empty).Trim();
        if (named.Length == 0 &&
            string.Equals(element.Kind, LogicElementKinds.Coil, StringComparison.OrdinalIgnoreCase))
        {
            named = (block.OutputTag ?? string.Empty).Trim();
        }

        return named.Length > 0
            ? ForNamed(block, named, areaTags)
            : ForPair(element.SourceId, element.ItemId);
    }

    public static LogicTagReference? ForNamed(
        LogicBlockDto block,
        string? tagName,
        LogicAreaTagsDto? areaTags)
    {
        string name = (tagName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return null;
        }

        string area = LogicAreaTags.AreaNameFor(block);
        LogicTagDto? entry = LogicAreaTags.FindTag(areaTags, name);
        string sourceId = (entry?.SourceId ?? string.Empty).Trim();
        string itemId = (entry?.ItemId ?? string.Empty).Trim();
        return new LogicTagReference
        {
            Named = true,
            Name = name,
            Area = area,
            SourceId = sourceId,
            ItemId = itemId,
            Key = LogicAreaTags.IsBound(entry)
                ? LogicTagReference.PairKey(sourceId, itemId)
                : LogicTagReference.NameKey(area, name)
        };
    }

    public static LogicTagReference? ForPair(string? sourceId, string? itemId)
    {
        string item = (itemId ?? string.Empty).Trim();
        if (item.Length == 0)
        {
            return null;
        }

        string source = (sourceId ?? string.Empty).Trim();
        return new LogicTagReference
        {
            SourceId = source,
            ItemId = item,
            Key = LogicTagReference.PairKey(source, item)
        };
    }
}

/// <summary>One block behind a tag: the roles it plays and the lines that reference the tag.</summary>
public sealed class LogicTagUsage
{
    internal LogicTagUsage(LogicBlockDto block) => Block = block;

    public LogicBlockDto Block { get; }

    /// <summary>"read", "write" or "output" — the roles this block plays for the tag.</summary>
    public List<string> Roles { get; } = new();

    /// <summary>The sentences / labels that read or write it, de-duplicated in index order.</summary>
    public List<string> Lines { get; } = new();

    public bool Drives => Roles.Contains("output", StringComparer.Ordinal);

    public bool Reads => Roles.Any(role => !string.Equals(role, "output", StringComparison.Ordinal));
}

/// <summary>
/// Every place the definitions reference a tag, indexed once per definitions set: a row asks
/// for its own count, the inspector asks for the blocks behind it. Mirrors the web Result tab's
/// tagUsageIndex: block networks and conditions are reads, actions are writes, and a block's
/// OUTPUT tag is the one thing that drives it; a coil is the output, not a read of itself.
/// </summary>
public sealed class LogicTagUsageIndex
{
    private readonly Dictionary<string, List<LogicTagUsage>> byKey_ = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<LogicAreaTagsDto> areaTags_;

    private LogicTagUsageIndex(IReadOnlyList<LogicAreaTagsDto> areaTags) => areaTags_ = areaTags;

    public static LogicTagUsageIndex Build(
        IEnumerable<LogicBlockDto>? blocks,
        IReadOnlyList<LogicAreaTagsDto>? areaTags)
    {
        IReadOnlyList<LogicAreaTagsDto> lists = areaTags ?? Array.Empty<LogicAreaTagsDto>();
        LogicTagUsageIndex index = new(lists);

        foreach (LogicBlockDto block in blocks ?? Enumerable.Empty<LogicBlockDto>())
        {
            LogicAreaTagsDto? blockTags = LogicAreaTags.Find(lists, LogicAreaTags.AreaNameFor(block));

            void AddReads(IEnumerable<LogicElementDto> roots)
            {
                foreach ((LogicElementDto element, int _) in LogicNetwork.Walk(roots))
                {
                    // The coil is the block's output, indexed once as the output role below.
                    if (string.Equals(element.Kind, LogicElementKinds.Coil, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    index.Add(block, LogicTagReferences.ForElement(block, element, blockTags), element.Text, "read");
                }
            }

            AddReads(block.Elements);
            foreach (LogicConditionDto condition in block.Conditions)
            {
                index.Add(block, Reference(block, blockTags, condition.Tag, condition.SourceId, condition.ItemId), condition.Text, "read");
            }

            foreach (LogicStepDto step in block.Steps)
            {
                AddReads(step.Elements);
                foreach (LogicConditionDto condition in step.Conditions)
                {
                    index.Add(block, Reference(block, blockTags, condition.Tag, condition.SourceId, condition.ItemId), condition.Text, "read");
                }

                index.Add(
                    block,
                    Reference(block, blockTags, step.CompletionTag, step.CompletionSourceId, step.CompletionItemId),
                    (string.IsNullOrWhiteSpace(step.Name) ? "Step" : step.Name.Trim()) + " completion",
                    "read");

                foreach (LogicActionDto action in step.Actions)
                {
                    index.Add(block, Reference(block, blockTags, action.Tag, action.SourceId, action.ItemId), action.Label, "write");
                }
            }

            foreach (LogicActionDto action in block.Actions)
            {
                index.Add(block, Reference(block, blockTags, action.Tag, action.SourceId, action.ItemId), action.Label, "write");
            }

            // The block's output is what drives its OUTPUT tag for every other block; the coil
            // itself carries no tag, so this is the one place that says who the source is.
            index.Add(
                block,
                LogicTagReferences.ForNamed(block, block.OutputTag, blockTags),
                "output " + ((block.OutputTag ?? string.Empty).Trim().Length > 0 ? block.OutputTag!.Trim() : "coil"),
                "output");
        }

        return index;
    }

    /// <summary>Every block that reads, writes or drives the tag, sorted by block name.</summary>
    public IReadOnlyList<LogicTagUsage> Uses(LogicTagReference? reference)
    {
        if (reference is null || !byKey_.TryGetValue(reference.Key, out List<LogicTagUsage>? uses))
        {
            return Array.Empty<LogicTagUsage>();
        }

        return uses
            .OrderBy(use => use.Block.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<LogicTagUsage> Drivers(LogicTagReference? reference) =>
        Uses(reference).Where(use => use.Drives).ToList();

    public IReadOnlyList<LogicTagUsage> Readers(LogicTagReference? reference) =>
        Uses(reference).Where(use => use.Reads).ToList();

    private static LogicTagReference? Reference(
        LogicBlockDto block,
        LogicAreaTagsDto? areaTags,
        string? tag,
        string? sourceId,
        string? itemId) =>
        LogicTagReferences.ForNamed(block, tag, areaTags) ?? LogicTagReferences.ForPair(sourceId, itemId);

    private void Add(LogicBlockDto block, LogicTagReference? reference, string? line, string role)
    {
        if (reference is null)
        {
            return;
        }

        if (!byKey_.TryGetValue(reference.Key, out List<LogicTagUsage>? uses))
        {
            uses = new List<LogicTagUsage>();
            byKey_[reference.Key] = uses;
        }

        LogicTagUsage? use = uses.FirstOrDefault(candidate => candidate.Block.Id == block.Id);
        if (use is null)
        {
            use = new LogicTagUsage(block);
            uses.Add(use);
        }

        if (!use.Roles.Contains(role, StringComparer.Ordinal))
        {
            use.Roles.Add(role);
        }

        string text = (line ?? string.Empty).Trim();
        if (text.Length > 0 && !use.Lines.Contains(text, StringComparer.Ordinal))
        {
            use.Lines.Add(text);
        }
    }
}
