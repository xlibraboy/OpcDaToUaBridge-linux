namespace OpcBridge.Client;

/// <summary>
/// The name book for tags sources: resolves a reference a block carries (a condition, a contact,
/// a step handshake, an action) to the source/item pair its machine's <see cref="LogicTagSourceDto"/>
/// holds for that name. Pure and shared by the bridge evaluator, the HMI editor and the phone, so
/// all three read a machine's tags the same way.
///
/// A reference that does not resolve is a normal, authorable state — the entry may be created
/// empty and assigned later — so callers report it in the operator's words rather than failing:
/// an unassigned entry reads "not assigned", a name missing from the source reads "not in
/// &lt;machine&gt;" (or "no tags source" when the block names no machine at all).
/// </summary>
public static class LogicTagSources
{
    /// <summary>The block's machine's tags source, matched by name case-insensitively.</summary>
    public static LogicTagSourceDto? FindSource(
        IEnumerable<LogicTagSourceDto>? sources,
        string? sourceName)
    {
        if (sources is null || string.IsNullOrWhiteSpace(sourceName))
        {
            return null;
        }

        string name = sourceName.Trim();
        foreach (LogicTagSourceDto source in sources)
        {
            if (string.Equals(source.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        return null;
    }

    /// <summary>One entry of a source, matched by name case-insensitively.</summary>
    public static LogicTagDto? FindTag(LogicTagSourceDto? source, string? tagName)
    {
        if (source is null || string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        string name = tagName.Trim();
        foreach (LogicTagDto tag in source.Tags)
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
    /// Resolves a reference to the pair its machine's tags source holds for it. False = the entry
    /// is missing or still unassigned (see <see cref="UnresolvedText"/> for the wording).
    /// </summary>
    public static bool TryResolve(
        LogicTagSourceDto? source,
        string? tagName,
        out string sourceId,
        out string itemId)
    {
        LogicTagDto? tag = FindTag(source, tagName);
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
    /// The entry's own spelling when the source holds the name (the store canonicalizes
    /// references on it), else the trimmed reference the author typed.
    /// </summary>
    public static string CanonicalName(LogicTagSourceDto? source, string? tagName)
    {
        LogicTagDto? tag = FindTag(source, tagName);
        return tag?.Name?.Trim() ?? tagName?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Why a reference reads unknown, in the operator's words: the entry exists but has no tag
    /// yet ("not assigned"), the name is not in the machine's source ("not in DRYEND"), or the
    /// block names no machine at all ("no tags source"). Pass the resolved source (null when the
    /// machine has none) alongside the machine name the block carries, so a typo and a missing
    /// assignment are told apart.
    /// </summary>
    public static string UnresolvedText(string? tagName, LogicTagSourceDto? source, string? sourceName)
    {
        if (source is null || FindTag(source, tagName) is null)
        {
            return MissingText(tagName, sourceName);
        }

        return Name(tagName) + " (not assigned)";
    }

    /// <summary>The operator's words for a name the machine's tags source does not hold.</summary>
    public static string MissingText(string? tagName, string? sourceName) =>
        string.IsNullOrWhiteSpace(sourceName)
            ? Name(tagName) + " (no tags source)"
            : Name(tagName) + " (not in " + sourceName.Trim() + ")";

    private static string Name(string? tagName) =>
        string.IsNullOrWhiteSpace(tagName) ? "(unnamed tag)" : tagName.Trim();

    /// <summary>
    /// Every entry name the block's references carry (conditions, element contacts, step
    /// handshakes, actions), deduplicated case-insensitively and in first-seen order — what the
    /// machine's tags source must hold, and what a "used by this block" marker matches.
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
