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

        string kind = block.Kind.ToLowerInvariant();
        bool isSequence = kind == LogicBlockKinds.Sequence;

        List<LogicConditionDto> conditions = new();
        if (!isSequence)
        {
            if (block.Conditions.Count == 0)
            {
                normalized = default!;
                error = "A block needs at least one condition.";
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
            Description = block.Description?.Trim() ?? string.Empty,
            Kind = kind,
            Enabled = block.Enabled,
            Order = block.Order,
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
            Conditions = conditions,
            NextStepText = NormalizeOptional(step.NextStepText),
            CompletionSourceId = completionSourceId,
            CompletionItemId = completionItemId,
            Actions = actions
        };
        error = null;
        return true;
    }

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

        normalized = new LogicConditionDto
        {
            Id = condition.Id == Guid.Empty ? Guid.NewGuid() : condition.Id,
            Text = text,
            SourceId = NormalizeSourceId(condition.SourceId),
            ItemId = itemId,
            Op = op,
            Value = LogicConditionOps.RequiresValue(op) ? condition.Value : null,
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
