namespace OpcBridge.Client;

/// <summary>
/// The name book for area tags: resolves a reference a block carries (a condition, a contact,
/// a step handshake, an action) to the source/item pair the block's area's
/// <see cref="LogicAreaTagsDto"/> holds for that name. Pure and shared by the bridge evaluator,
/// the HMI editor and the phone, so all three read an area's tags the same way.
///
/// <see cref="AreaNameFor"/> is the one place that decides which area a block reads: its
/// <see cref="LogicBlockDto.Area"/>, else the name it stores
/// (<see cref="LogicBlockDto.TagSource"/>), else its interlock group — the last two are the
/// legacy fallbacks, so blocks authored before areas existed resolve unchanged.
///
/// A reference that does not resolve is a normal, authorable state — the entry may be created
/// empty and assigned later — so callers report it in the operator's words rather than failing:
/// an unassigned entry reads "not assigned", a name missing from the area's tags reads "not in
/// &lt;area&gt;" (or "no area" when the block names no area at all).
/// </summary>
public static class LogicAreaTags
{
    /// <summary>
    /// The area a block reads its named tags from: its area, else the area tags it stores,
    /// else its interlock group (legacy), trimmed — empty when the block names none of them.
    /// </summary>
    public static string AreaNameFor(LogicBlockDto? block)
    {
        if (block is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(block.Area))
        {
            return block.Area.Trim();
        }

        return string.IsNullOrWhiteSpace(block.TagSource)
            ? block.Group?.Trim() ?? string.Empty
            : block.TagSource.Trim();
    }

    /// <summary>The area's tags, matched by name case-insensitively.</summary>
    public static LogicAreaTagsDto? Find(
        IEnumerable<LogicAreaTagsDto>? lists,
        string? areaName)
    {
        if (lists is null || string.IsNullOrWhiteSpace(areaName))
        {
            return null;
        }

        string name = areaName.Trim();
        foreach (LogicAreaTagsDto list in lists)
        {
            if (string.Equals(list.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return list;
            }
        }

        return null;
    }

    /// <summary>One entry of an area's tags, matched by name case-insensitively.</summary>
    public static LogicTagDto? FindTag(LogicAreaTagsDto? tags, string? tagName)
    {
        if (tags is null || string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        string name = tagName.Trim();
        foreach (LogicTagDto tag in tags.Tags)
        {
            if (string.Equals(tag.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        return null;
    }

    /// <summary>True when the entry carries a source/item pair to read.</summary>
    public static bool IsBound(LogicTagDto? tag) =>
        tag is not null
        && !string.IsNullOrWhiteSpace(tag.SourceId)
        && !string.IsNullOrWhiteSpace(tag.ItemId);

    /// <summary>
    /// Resolves a reference to the pair the area's tags hold for it. False = the entry is
    /// missing or still unassigned (see <see cref="UnresolvedText"/> for the wording).
    /// </summary>
    public static bool TryResolve(
        LogicAreaTagsDto? tags,
        string? tagName,
        out string sourceId,
        out string itemId)
    {
        LogicTagDto? tag = FindTag(tags, tagName);
        if (!IsBound(tag))
        {
            sourceId = string.Empty;
            itemId = string.Empty;
            return false;
        }

        sourceId = tag!.SourceId.Trim();
        itemId = tag!.ItemId.Trim();
        return true;
    }

    /// <summary>
    /// The entry's own spelling when the area's tags hold the name (the store canonicalizes
    /// references on it), else the trimmed reference the author typed.
    /// </summary>
    public static string CanonicalName(LogicAreaTagsDto? tags, string? tagName)
    {
        LogicTagDto? tag = FindTag(tags, tagName);
        return tag?.Name?.Trim() ?? tagName?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Why a reference reads unknown, in the operator's words: the entry exists but has no tag
    /// yet ("not assigned"), the name is not in the area's tags ("not in DRY END"), or the block
    /// names no area at all ("no area"). Pass the resolved list (null when the area has none)
    /// alongside the area name the block carries, so a typo and a missing assignment are told
    /// apart.
    /// </summary>
    public static string UnresolvedText(string? tagName, LogicAreaTagsDto? tags, string? areaName)
    {
        if (tags is null || FindTag(tags, tagName) is null)
        {
            return MissingText(tagName, areaName);
        }

        return Name(tagName) + " (not assigned)";
    }

    /// <summary>The operator's words for a name the area's tags do not hold.</summary>
    public static string MissingText(string? tagName, string? areaName) =>
        string.IsNullOrWhiteSpace(areaName)
            ? Name(tagName) + " (no area)"
            : Name(tagName) + " (not in " + areaName.Trim() + ")";

    private static string Name(string? tagName) =>
        string.IsNullOrWhiteSpace(tagName) ? "(unnamed tag)" : tagName.Trim();

    /// <summary>
    /// Every entry name the block's references carry (conditions, element contacts, step
    /// handshakes, actions), deduplicated case-insensitively and in first-seen order — what the
    /// area's tags must hold, and what a "used by this block" marker matches.
    /// </summary>
    public static List<string> UsedNames(LogicBlockDto? block)
    {
        List<string> names = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        if (block is null)
        {
            return names;
        }

        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            string trimmed = name.Trim();
            if (seen.Add(trimmed))
            {
                names.Add(trimmed);
            }
        }

        foreach (LogicConditionDto condition in block.Conditions)
        {
            Add(condition.Tag);
        }

        Add(block.OutputTag);

        foreach (LogicStepDto step in block.Steps)
        {
            Add(step.CompletionTag);
            foreach (LogicConditionDto condition in step.Conditions)
            {
                Add(condition.Tag);
            }

            foreach ((LogicElementDto element, int _) in LogicNetwork.Walk(step.Elements))
            {
                Add(element.Tag);
            }
        }

        foreach ((LogicElementDto element, int _) in LogicNetwork.Walk(block.Elements))
        {
            Add(element.Tag);
        }

        foreach (LogicActionDto action in block.Actions)
        {
            Add(action.Tag);
        }

        return names;
    }
}
