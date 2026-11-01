using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// Builds element rows from a block's network and every sequence step's network. The detail
/// screen and the persistent Elements list share this, so a row's label, tag identity and
/// indentation read the same wherever it appears.
/// </summary>
public static class LogicElementRows
{
    /// <summary>The block's network rows followed by every step's network rows.</summary>
    public static List<LogicElementRowViewModel> Build(
        LogicBlockDto block,
        LogicAreaTagsDto? areaTags,
        MultiBridgeTagCache? tags,
        string bridgeKey)
    {
        List<LogicElementRowViewModel> rows = Walk(
            block, null, 0, LogicNetwork.For(block), areaTags, tags, bridgeKey);

        int number = 0;
        foreach (LogicStepDto step in block.Steps)
        {
            number++;
            rows.AddRange(Walk(block, step, number, LogicNetwork.For(step), areaTags, tags, bridgeKey));
        }

        return rows;
    }

    /// <summary>One network's rows, carrying the block/step context the persistent list shows.</summary>
    public static List<LogicElementRowViewModel> Walk(
        LogicBlockDto block,
        LogicStepDto? step,
        int stepNumber,
        IEnumerable<LogicElementDto> roots,
        LogicAreaTagsDto? areaTags,
        MultiBridgeTagCache? tags,
        string bridgeKey)
    {
        List<LogicElementRowViewModel> rows = new();
        foreach ((LogicElementDto element, int depth) in LogicNetwork.Walk(roots))
        {
            LogicTagReference? reference = LogicTagReferences.ForElement(block, element, areaTags);
            string label = ResolveLabel(block, areaTags, tags, bridgeKey, element, reference);
            rows.Add(new LogicElementRowViewModel(element, depth, label, reference)
            {
                BlockId = block.Id,
                BlockName = block.Name,
                BlockKind = block.Kind,
                StepId = step?.Id,
                StepLabel = StepLabel(step, stepNumber)
            });
        }

        return rows;
    }

    /// <summary>A row's tag label: the direct pair, or the named entry with what it is bound to.</summary>
    public static string ResolveLabel(
        LogicBlockDto block,
        LogicAreaTagsDto? areaTags,
        MultiBridgeTagCache? tags,
        string bridgeKey,
        LogicElementDto element,
        LogicTagReference? reference)
    {
        if (!string.IsNullOrWhiteSpace(element.Relay))
        {
            // A relay contact and a coil both name their relay where a tag contact names its tag.
            return "relay " + element.Relay;
        }

        if (reference is { Named: true })
        {
            return ResolvedLabel(block, areaTags, tags, bridgeKey, reference.Name, null, null);
        }

        if (reference is not null)
        {
            return ResolvedLabel(block, areaTags, tags, bridgeKey, null, reference.SourceId, reference.ItemId);
        }

        return string.Empty;
    }

    /// <summary>A tag's label: the direct tag, or the named one with what it is bound to.</summary>
    public static string ResolvedLabel(
        LogicBlockDto block,
        LogicAreaTagsDto? areaTags,
        MultiBridgeTagCache? tags,
        string bridgeKey,
        string? tag,
        string? sourceId,
        string? itemId)
    {
        if (!string.IsNullOrWhiteSpace(tag))
        {
            string name = tag.Trim();
            return TryResolve(areaTags, tag, null, null, out string resolvedSourceId, out string resolvedItemId)
                ? name + " · " + TagLabel(tags, bridgeKey, resolvedSourceId, resolvedItemId)
                : LogicAreaTags.UnresolvedText(tag, areaTags, LogicAreaTags.AreaNameFor(block));
        }

        return TagLabel(tags, bridgeKey, sourceId ?? string.Empty, itemId ?? string.Empty);
    }

    /// <summary>"DisplayName · SourceName", falling back to the raw pair when the tag is unknown.</summary>
    public static string TagLabel(MultiBridgeTagCache? tags, string bridgeKey, string sourceId, string itemId)
    {
        if (tags is not null &&
            tags.TryGet(TagBindingKey.Create(bridgeKey, sourceId, itemId), out MultiBridgeTagEntry? entry) &&
            entry is not null)
        {
            return $"{entry.DisplayName} · {entry.SourceName}";
        }

        return string.IsNullOrWhiteSpace(sourceId) ? itemId : $"{itemId} · {sourceId}";
    }

    /// <summary>"Step 2 · Open valve", or "Step 2" when the step is unnamed.</summary>
    public static string StepLabel(LogicStepDto? step, int stepNumber)
    {
        if (step is null)
        {
            return string.Empty;
        }

        string name = (step.Name ?? string.Empty).Trim();
        return "Step " + stepNumber + (name.Length > 0 ? " · " + name : string.Empty);
    }

    private static bool TryResolve(
        LogicAreaTagsDto? areaTags,
        string? tag,
        string? sourceId,
        string? itemId,
        out string resolvedSourceId,
        out string resolvedItemId)
    {
        if (!string.IsNullOrWhiteSpace(tag))
        {
            return LogicAreaTags.TryResolve(areaTags, tag, out resolvedSourceId, out resolvedItemId);
        }

        resolvedSourceId = sourceId?.Trim() ?? string.Empty;
        resolvedItemId = itemId?.Trim() ?? string.Empty;
        return resolvedSourceId.Length > 0 && resolvedItemId.Length > 0;
    }
}
