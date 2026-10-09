using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.Client;
using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Persists the plant logic blocks authored in the dashboard's Logic tab
/// (<c>logic.json</c> in the data directory). Mirrors <see cref="InterlinkStore"/>: one
/// coarse lock, an in-memory version counter, whole-file persistence and a
/// validate-all-or-nothing save. Unlike the interlink store it raises
/// <see cref="Changed"/> so the HMI broadcaster re-evaluates and pushes the new state.
/// </summary>
public sealed class LogicStore
{
    private const int MaxTagsPerBlock = 8;
    private const int MaxTagLength = 24;

    /// <summary>Longest interlock group name a block may carry (the phone's heading).</summary>
    private const int MaxBlockGroupLength = 40;

    /// <summary>Longest OR group label a condition may carry.</summary>
    private const int MaxOrGroupLength = 24;

    /// <summary>Longest hold timer a contact may carry (one hour).</summary>
    private const int MaxHoldMs = 3_600_000;

    /// <summary>Longest counter preset a CTU / CTD may carry.</summary>
    private const int MaxCounterPv = 1_000_000;

    /// <summary>Deepest an IEC network may nest, and how many elements one block may hold.</summary>
    private const int MaxElementDepth = 8;
    private const int MaxElementsPerBlock = 64;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object sync_ = new();
    private readonly string persist_path_;
    private List<LogicBlockDto> blocks_;
    private long version_;

    public LogicStore(IOptions<BridgeOptions> options)
    {
        _ = options;
        persist_path_ = DataDirectory.Combine("logic.json");
        blocks_ = LoadFromDisk() ?? new List<LogicBlockDto>();
    }

    /// <summary>Raised after a change, with the new version (outside the lock).</summary>
    public event Action<long>? Changed;

    public (IReadOnlyList<LogicBlockDto> Blocks, long Version) GetSnapshot()
    {
        lock (sync_)
        {
            return (blocks_.ToArray(), version_);
        }
    }

    public long SetAll(IEnumerable<LogicBlockDto> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        lock (sync_)
        {
            blocks_ = NormalizeAllOrThrow(blocks);
            version_++;
            Persist();
        }

        Changed?.Invoke(version_);
        return version_;
    }

    /// <summary>
    /// Insert or replace by id — the dashboard's Save for a new or edited block.
    /// <paramref name="saved"/> is the normalized block as stored (ids generated, text trimmed).
    /// </summary>
    public bool TrySave(LogicBlockDto block, out LogicBlockDto saved, out long version, out string? error)
    {
        long newVersion;
        LogicBlockDto stored;
        lock (sync_)
        {
            if (!TryNormalize(block, out LogicBlockDto normalized, out error))
            {
                saved = block;
                version = version_;
                return false;
            }

            if (HasNameConflict(normalized.Name, normalized.Id))
            {
                saved = block;
                version = version_;
                error = "Block name already exists.";
                return false;
            }

            int index = blocks_.FindIndex(existing => existing.Id == normalized.Id);
            if (index < 0)
            {
                blocks_.Add(normalized);
            }
            else
            {
                blocks_[index] = normalized;
            }

            version_++;
            Persist();
            newVersion = version_;
            stored = normalized;
            error = null;
        }

        Changed?.Invoke(newVersion);
        saved = stored;
        version = newVersion;
        return true;
    }

    public bool TryRemove(Guid id, out long version)
    {
        long newVersion;
        lock (sync_)
        {
            int removed = blocks_.RemoveAll(existing => existing.Id == id);
            if (removed == 0)
            {
                version = version_;
                return false;
            }

            version_++;
            Persist();
            newVersion = version_;
        }

        Changed?.Invoke(newVersion);
        version = newVersion;
        return true;
    }

    private bool HasNameConflict(string name, Guid currentId)
    {
        return blocks_.Any(existing =>
            existing.Id != currentId &&
            string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static List<LogicBlockDto> NormalizeAllOrThrow(IEnumerable<LogicBlockDto> blocks)
    {
        List<LogicBlockDto> normalized = new();
        HashSet<Guid> ids = new();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (LogicBlockDto block in blocks)
        {
            if (!TryNormalize(block, out LogicBlockDto normalizedBlock, out string? error))
            {
                throw new InvalidOperationException(error);
            }

            if (!ids.Add(normalizedBlock.Id))
            {
                throw new InvalidOperationException("Block already exists.");
            }

            if (!names.Add(normalizedBlock.Name))
            {
                throw new InvalidOperationException("Block name already exists.");
            }

            normalized.Add(normalizedBlock);
        }

        return normalized;
    }

    private static bool TryNormalize(LogicBlockDto block, out LogicBlockDto normalized, out string? error)
    {
        ArgumentNullException.ThrowIfNull(block);

        string name = block.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > 64)
        {
            normalized = default!;
            error = "Block name is required (max 64 characters).";
            return false;
        }

        if (!LogicBlockKinds.IsValid(block.Kind))
        {
            normalized = default!;
            error = "Block kind must be interlock, permissive or sequence.";
            return false;
        }

        if (!TryNormalizeTags(block.Tags, out List<string> tags, out error))
        {
            normalized = default!;
            return false;
        }

        string blockGroup = block.Group?.Trim() ?? string.Empty;
        if (blockGroup.Length > MaxBlockGroupLength)
        {
            normalized = default!;
            error = "An interlock group can be at most 40 characters.";
            return false;
        }

        string kind = block.Kind.ToLowerInvariant();
        bool isSequence = kind == LogicBlockKinds.Sequence;

        // A block carries its logic either as an IEC network or as the simple conditions form;
        // the two are alternatives, never both, so there is one source of truth to evaluate.
        List<LogicElementDto> elements = new();
        if (!TryNormalizeElements(block.Elements, out elements, out error))
        {
            normalized = default!;
            return false;
        }

        List<LogicConditionDto> conditions = new();
        if (!isSequence)
        {
            if (elements.Count > 0 && block.Conditions.Count > 0)
            {
                normalized = default!;
                error = "Use either conditions or an IEC network, not both.";
                return false;
            }

            if (elements.Count == 0 && block.Conditions.Count == 0)
            {
                normalized = default!;
                error = "A block needs at least one condition or an IEC network.";
                return false;
            }

            foreach (LogicConditionDto condition in block.Conditions)
            {
                if (!TryNormalizeCondition(condition, out LogicConditionDto normalizedCondition, out error))
                {
                    normalized = default!;
                    return false;
                }

                conditions.Add(normalizedCondition);
            }
        }
        else if (elements.Count > 0 || block.Conditions.Count > 0)
        {
            normalized = default!;
            error = "A sequence carries its logic in its steps.";
            return false;
        }

        List<LogicStepDto> steps = new();
        if (isSequence)
        {
            if (block.Steps.Count == 0)
            {
                normalized = default!;
                error = "A sequence needs at least one step.";
                return false;
            }

            HashSet<string> stepNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (LogicStepDto step in block.Steps)
            {
                if (!TryNormalizeStep(step, out LogicStepDto normalizedStep, out error))
                {
                    normalized = default!;
                    return false;
                }

                if (!stepNames.Add(normalizedStep.Name))
                {
                    normalized = default!;
                    error = "Step names must be unique within a sequence.";
                    return false;
                }

                steps.Add(normalizedStep);
            }
        }

        List<LogicActionDto> actions = new();
        foreach (LogicActionDto action in block.Actions)
        {
            if (!TryNormalizeAction(action, out LogicActionDto normalizedAction, out error))
            {
                normalized = default!;
                return false;
            }

            actions.Add(normalizedAction);
        }

        normalized = new LogicBlockDto
        {
            Id = block.Id == Guid.Empty ? Guid.NewGuid() : block.Id,
            Name = name,
            Group = blockGroup,
            Description = block.Description?.Trim() ?? string.Empty,
            Kind = kind,
            Enabled = block.Enabled,
            Order = block.Order,
            Tags = tags,
            Elements = elements,
            Conditions = conditions,
            Steps = steps,
            Actions = actions
        };
        error = null;
        return true;
    }

    private static bool TryNormalizeStep(LogicStepDto step, out LogicStepDto normalized, out string? error)
    {
        string name = step.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > 64)
        {
            normalized = default!;
            error = "Step name is required (max 64 characters).";
            return false;
        }

        List<LogicElementDto> stepElements = new();
        if (!TryNormalizeElements(step.Elements, out stepElements, out error))
        {
            normalized = default!;
            return false;
        }

        if (stepElements.Count > 0 && step.Conditions.Count > 0)
        {
            normalized = default!;
            error = "Use either conditions or an IEC network, not both.";
            return false;
        }

        List<LogicConditionDto> conditions = new();
        foreach (LogicConditionDto condition in step.Conditions)
        {
            if (!TryNormalizeCondition(condition, out LogicConditionDto normalizedCondition, out error))
            {
                normalized = default!;
                return false;
            }

            conditions.Add(normalizedCondition);
        }

        List<LogicActionDto> actions = new();
        foreach (LogicActionDto action in step.Actions)
        {
            if (!TryNormalizeAction(action, out LogicActionDto normalizedAction, out error))
            {
                normalized = default!;
                return false;
            }

            actions.Add(normalizedAction);
        }

        string? completionSourceId = NormalizeOptional(step.CompletionSourceId);
        string? completionItemId = NormalizeOptional(step.CompletionItemId);
        if ((completionSourceId is null) != (completionItemId is null))
        {
            normalized = default!;
            error = "A completion handshake needs both a source and a tag.";
            return false;
        }

        if (completionSourceId is not null)
        {
            completionSourceId = NormalizeSourceId(completionSourceId);
        }

        normalized = new LogicStepDto
        {
            Id = step.Id == Guid.Empty ? Guid.NewGuid() : step.Id,
            Name = name,
            Elements = stepElements,
            Conditions = conditions,
            NextStepText = NormalizeOptional(step.NextStepText),
            CompletionSourceId = completionSourceId,
            CompletionItemId = completionItemId,
            Actions = actions
        };
        error = null;
        return true;
    }

    /// <summary>
    /// Normalizes a network: element kinds, arities and constants checked, ids generated, the
    /// contact fields cleared on gates and blocks (and the presets cleared where they do not
    /// apply), depth and element count bounded so a hand-written file cannot build an unbounded
    /// tree.
    /// </summary>
    private static bool TryNormalizeElements(
        IReadOnlyList<LogicElementDto>? elements,
        out List<LogicElementDto> normalized,
        out string? error)
    {
        normalized = new List<LogicElementDto>();
        int budget = MaxElementsPerBlock;

        foreach (LogicElementDto element in elements ?? Array.Empty<LogicElementDto>())
        {
            if (!TryNormalizeElement(element, 0, ref budget, out LogicElementDto child, out error))
            {
                return false;
            }

            normalized.Add(child);
        }

        error = null;
        return true;
    }

    private static bool TryNormalizeElement(
        LogicElementDto element,
        int depth,
        ref int budget,
        out LogicElementDto normalized,
        out string? error)
    {
        if (depth > MaxElementDepth)
        {
            normalized = default!;
            error = "A network can nest at most 8 levels.";
            return false;
        }

        if (budget-- <= 0)
        {
            normalized = default!;
            error = "A block can hold at most 64 elements.";
            return false;
        }

        if (!LogicElementKinds.IsValid(element.Kind))
        {
            normalized = default!;
            error = "Unknown element kind.";
            return false;
        }

        string kind = element.Kind.ToLowerInvariant();
        string text = element.Text?.Trim() ?? string.Empty;
        if (text.Length > 200)
        {
            normalized = default!;
            error = "Element text is at most 200 characters.";
            return false;
        }

        if (!LogicConditionSeverities.IsValid(element.Severity))
        {
            normalized = default!;
            error = "Element severity must be block or warn.";
            return false;
        }

        if (kind == LogicElementKinds.Contact)
        {
            if (text.Length == 0)
            {
                normalized = default!;
                error = "A contact needs its operator sentence.";
                return false;
            }

            string contactItemId = element.ItemId?.Trim() ?? string.Empty;
            if (contactItemId.Length == 0)
            {
                normalized = default!;
                error = "A contact needs a tag.";
                return false;
            }

            if (!LogicConditionOps.IsValid(element.Op))
            {
                normalized = default!;
                error = "A contact must be on, off, gt, lt or eq.";
                return false;
            }

            if (element.Inputs.Count > 0)
            {
                normalized = default!;
                error = "A contact reads one tag and takes no inputs.";
                return false;
            }

            string contactOp = element.Op.ToLowerInvariant();
            normalized = new LogicElementDto
            {
                Id = element.Id == Guid.Empty ? Guid.NewGuid() : element.Id,
                Kind = LogicElementKinds.Contact,
                Text = text,
                SourceId = NormalizeSourceId(element.SourceId),
                ItemId = contactItemId,
                Op = contactOp,
                Value = LogicConditionOps.RequiresValue(contactOp) ? element.Value : null,
                Severity = element.Severity.ToLowerInvariant(),
                NextStepText = NormalizeOptional(element.NextStepText)
            };
            error = null;
            return true;
        }

        (int minInputs, int maxInputs) = LogicElementKinds.InputRange(kind);
        if (element.Inputs.Count < minInputs || element.Inputs.Count > maxInputs)
        {
            normalized = default!;
            error = minInputs == maxInputs
                ? $"{KindName(kind)} takes exactly {minInputs} input(s)."
                : $"{KindName(kind)} takes between {minInputs} and {maxInputs} inputs.";
            return false;
        }

        if (element.PtMs is < 0 or > MaxHoldMs)
        {
            normalized = default!;
            error = "A timer preset must be between 0 and 3,600,000 ms.";
            return false;
        }

        if (element.Pv is < 0 or > MaxCounterPv)
        {
            normalized = default!;
            error = "A counter preset must be between 0 and 1,000,000.";
            return false;
        }

        List<LogicElementDto> inputs = new();
        foreach (LogicElementDto input in element.Inputs)
        {
            if (!TryNormalizeElement(input, depth + 1, ref budget, out LogicElementDto child, out error))
            {
                normalized = default!;
                return false;
            }

            inputs.Add(child);
        }

        normalized = new LogicElementDto
        {
            Id = element.Id == Guid.Empty ? Guid.NewGuid() : element.Id,
            Kind = kind,
            Text = text,
            // A gate or block combines its inputs; only a contact reads a tag, and only a timer
            // or counter carries a preset.
            PtMs = LogicElementKinds.IsTimer(kind) ? element.PtMs : 0,
            Pv = LogicElementKinds.IsCounter(kind) ? element.Pv : 0,
            Inputs = inputs,
            Severity = element.Severity.ToLowerInvariant(),
            NextStepText = NormalizeOptional(element.NextStepText)
        };
        error = null;
        return true;
    }

    private static string KindName(string kind) => kind switch
    {
        LogicElementKinds.And => "AND",
        LogicElementKinds.Or => "OR",
        LogicElementKinds.Xor => "XOR",
        LogicElementKinds.Not => "NOT",
        LogicElementKinds.Ton => "TON",
        LogicElementKinds.Tof => "TOF",
        LogicElementKinds.Tp => "TP",
        LogicElementKinds.Ctu => "CTU",
        LogicElementKinds.Ctd => "CTD",
        LogicElementKinds.Sr => "SR",
        LogicElementKinds.Rs => "RS",
        LogicElementKinds.RisingEdge => "R_TRIG",
        LogicElementKinds.FallingEdge => "F_TRIG",
        _ => "The element"
    };

    private static bool TryNormalizeCondition(LogicConditionDto condition, out LogicConditionDto normalized, out string? error)
    {
        string text = condition.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > 200)
        {
            normalized = default!;
            error = "Condition text is required (max 200 characters).";
            return false;
        }

        string itemId = condition.ItemId?.Trim() ?? string.Empty;
        if (itemId.Length == 0)
        {
            normalized = default!;
            error = "Condition tag is required.";
            return false;
        }

        if (!LogicConditionOps.IsValid(condition.Op))
        {
            normalized = default!;
            error = "Condition operator must be on, off, gt, lt or eq.";
            return false;
        }

        string op = condition.Op.ToLowerInvariant();
        if (LogicConditionOps.RequiresValue(op) && condition.Value is null)
        {
            normalized = default!;
            error = "A numeric comparison needs a value.";
            return false;
        }

        if (!LogicConditionSeverities.IsValid(condition.Severity))
        {
            normalized = default!;
            error = "Condition severity must be block or warn.";
            return false;
        }

        if (condition.HoldMs is < 0 or > MaxHoldMs)
        {
            normalized = default!;
            error = "A hold timer must be between 0 and 3,600,000 ms.";
            return false;
        }

        string group = condition.Group?.Trim() ?? string.Empty;
        if (group.Length > MaxOrGroupLength)
        {
            normalized = default!;
            error = "An OR group can be at most 24 characters.";
            return false;
        }

        normalized = new LogicConditionDto
        {
            Id = condition.Id == Guid.Empty ? Guid.NewGuid() : condition.Id,
            Text = text,
            SourceId = NormalizeSourceId(condition.SourceId),
            ItemId = itemId,
            Op = op,
            Value = LogicConditionOps.RequiresValue(op) ? condition.Value : null,
            Group = group,
            HoldMs = condition.HoldMs,
            NextStepText = NormalizeOptional(condition.NextStepText),
            Severity = condition.Severity.ToLowerInvariant()
        };
        error = null;
        return true;
    }

    private static bool TryNormalizeAction(LogicActionDto action, out LogicActionDto normalized, out string? error)
    {
        string label = action.Label?.Trim() ?? string.Empty;
        if (label.Length == 0 || label.Length > 32)
        {
            normalized = default!;
            error = "Action label is required (max 32 characters).";
            return false;
        }

        string itemId = action.ItemId?.Trim() ?? string.Empty;
        if (itemId.Length == 0)
        {
            normalized = default!;
            error = "Action tag is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(action.Value))
        {
            normalized = default!;
            error = "Action value is required.";
            return false;
        }

        normalized = new LogicActionDto
        {
            Label = label,
            SourceId = NormalizeSourceId(action.SourceId),
            ItemId = itemId,
            Value = action.Value.Trim(),
            Confirm = action.Confirm
        };
        error = null;
        return true;
    }

    /// <summary>
    /// Trims the block's tags, drops blanks and case-insensitive duplicates (first spelling
    /// wins) and bounds the set: at most <see cref="MaxTagsPerBlock"/> tags of at most
    /// <see cref="MaxTagLength"/> characters each — a longer list is a save error, not a
    /// silent truncation.
    /// </summary>
    private static bool TryNormalizeTags(IReadOnlyList<string>? tags, out List<string> normalized, out string? error)
    {
        normalized = new List<string>();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string? raw in tags ?? Array.Empty<string>())
        {
            string tag = raw?.Trim() ?? string.Empty;
            if (tag.Length == 0 || !seen.Add(tag))
            {
                continue;
            }

            if (tag.Length > MaxTagLength)
            {
                error = "A tag can be at most 24 characters.";
                return false;
            }

            if (normalized.Count == MaxTagsPerBlock)
            {
                error = "A block can carry at most 8 tags.";
                return false;
            }

            normalized.Add(tag);
        }

        error = null;
        return true;
    }

    private static string? NormalizeOptional(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string NormalizeSourceId(string? sourceId)
    {
        string value = sourceId?.Trim() ?? string.Empty;
        return value.Length == 0 ? DaRuntimeSettings.DefaultSourceId : value;
    }

    private void Persist()
    {
        try
        {
            string json = JsonSerializer.Serialize(blocks_, JsonOptions);
            File.WriteAllText(persist_path_, json);
        }
        catch
        {
        }
    }

    private List<LogicBlockDto>? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(persist_path_))
            {
                return null;
            }

            string json = File.ReadAllText(persist_path_);
            List<LogicBlockDto>? loaded = JsonSerializer.Deserialize<List<LogicBlockDto>>(json);
            if (loaded is null)
            {
                return null;
            }

            // Drop blocks that no longer validate so one bad hand-edit cannot take the
            // whole store down, then rewrite the file clean.
            List<LogicBlockDto> sanitized = new();
            foreach (LogicBlockDto block in loaded)
            {
                if (TryNormalize(block, out LogicBlockDto normalized, out _))
                {
                    sanitized.Add(normalized);
                }
            }

            if (sanitized.Count != loaded.Count)
            {
                try
                {
                    File.WriteAllText(persist_path_, JsonSerializer.Serialize(sanitized, JsonOptions));
                }
                catch
                {
                }
            }

            return sanitized;
        }
        catch
        {
            return null;
        }
    }
}
