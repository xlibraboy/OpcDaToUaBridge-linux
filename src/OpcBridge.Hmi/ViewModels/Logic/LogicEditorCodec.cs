using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// Loads stored blocks into editable rows and builds the payload back. The collect rules keep the
/// three forms exclusive the way the store validates them: a sequence carries steps only; element
/// rows present mean the network form (conditions and steps empty); otherwise the flat conditions
/// form. Step action buttons are not authored in the editor and are carried through a save
/// untouched.
/// </summary>
public static class LogicEditorCodec
{
    /// <summary>A fresh block: interlock, enabled, one empty condition, ordered after the current ones.</summary>
    public static LogicBlockDto NewDraftBlock(int order) => new()
    {
        Kind = LogicBlockKinds.Interlock,
        Enabled = true,
        Order = order,
        Conditions =
        {
            new LogicConditionDto
            {
                Id = Guid.NewGuid(),
                Op = LogicConditionOps.On,
                Severity = LogicConditionSeverities.Block
            }
        }
    };

    public static List<LogicConditionRowViewModel> LoadConditions(
        IEnumerable<LogicConditionDto> conditions,
        LogicTagCatalog catalog,
        Action<LogicConditionRowViewModel>? onRemove) =>
        conditions.Select(condition => new LogicConditionRowViewModel(condition, catalog, onRemove)).ToList();

    /// <summary>One row per element, depth-first in drawing order, indented by its depth.</summary>
    public static List<LogicElementRowViewModel> LoadElements(
        IReadOnlyList<LogicElementDto> elements,
        LogicTagCatalog catalog,
        Action<LogicElementRowViewModel>? onRemove,
        IEnumerable<string>? relayNames = null,
        IReadOnlyDictionary<string, LogicRelayDto>? relayBindings = null)
    {
        var rows = new List<LogicElementRowViewModel>();
        foreach ((LogicElementDto element, int depth) in LogicNetwork.Walk(elements))
        {
            rows.Add(new LogicElementRowViewModel(element, depth, catalog, onRemove, relayNames, relayBindings));
        }

        return rows;
    }

    /// <summary>
    /// The relay bindings the rows declare, one per relay — every row that names one M shows the
    /// same binding, so the first wins. A row whose M has no tag yet comes back with an empty
    /// item id; the caller drops it, which is how a binding is removed.
    /// </summary>
    public static List<LogicRelayDto> RelayBindingsFromRows(IEnumerable<LogicElementRowViewModel> rows)
    {
        List<LogicRelayDto> bindings = new();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (LogicElementRowViewModel row in rows)
        {
            LogicRelayDto? binding = row.BindingFromRow();
            if (binding is null || !names.Add(binding.Name))
            {
                continue;
            }

            bindings.Add(binding);
        }

        return bindings;
    }

    /// <summary>True when two binding tables say the same thing (order-insensitive).</summary>
    public static bool SameRelayBindings(IReadOnlyList<LogicRelayDto> first, IReadOnlyList<LogicRelayDto> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        Dictionary<string, LogicRelayDto> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (LogicRelayDto relay in second)
        {
            byName[relay.Name] = relay;
        }

        foreach (LogicRelayDto relay in first)
        {
            if (!byName.TryGetValue(relay.Name, out LogicRelayDto? other)
                || !string.Equals(relay.SourceId, other.SourceId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(relay.ItemId, other.ItemId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    LogicRelayWrites.Normalize(relay.Write),
                    LogicRelayWrites.Normalize(other.Write),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Loads stored steps into editable rows. The owner supplies the live steps collection (rows
    /// resolve their own position in it) and may attach its dirty tracker to the returned rows;
    /// null skips the wiring for read-only or round-trip use.
    /// </summary>
    public static List<LogicStepRowViewModel> LoadSteps(
        IEnumerable<LogicStepDto> steps,
        LogicTagCatalog catalog,
        ObservableCollection<LogicStepRowViewModel>? owner,
        Action<LogicStepRowViewModel>? onRemove,
        Action? onMoved)
    {
        var rows = new List<LogicStepRowViewModel>();
        foreach (LogicStepDto step in steps)
        {
            rows.Add(new LogicStepRowViewModel(step, catalog, owner ?? new ObservableCollection<LogicStepRowViewModel>(), onRemove, onMoved));
        }

        return rows;
    }

    public static List<LogicActionRowViewModel> LoadActions(
        IEnumerable<LogicActionDto> actions,
        LogicTagCatalog catalog,
        Action<LogicActionRowViewModel>? onRemove) =>
        actions.Select(action => new LogicActionRowViewModel(action, catalog, onRemove)).ToList();

    /// <summary>Comma-separated labels, trimmed, blanks dropped (the store deduplicates).</summary>
    public static List<string> ParseTags(string text) =>
        text.Split(',')
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .ToList();

    public static LogicConditionDto ConditionFromRow(LogicConditionRowViewModel row) => new()
    {
        Id = row.ConditionId,
        Text = row.Text.Trim(),
        SourceId = row.SourceId,
        ItemId = row.ItemId,
        Op = ValidOp(row.Op?.Value),
        Value = NumericValue(row.Op?.Value, row.ValueText),
        Group = row.Group.Trim(),
        HoldMs = Milliseconds(row.HoldSeconds),
        NextStepText = TrimmedOrNull(row.NextStepText),
        Severity = ValidSeverity(row.Severity?.Value)
    };

    /// <summary>
    /// One element from its row. Only the fields that belong to the kind are set — a gate keeps the
    /// DTO defaults, exactly like the bridge's own store does. A relay on a contact makes it a
    /// relay contact (tag fields cleared) and a relay on a coil is the relay it drives.
    /// </summary>
    public static LogicElementDto ElementFromRow(LogicElementRowViewModel row)
    {
        string kind = LogicElementKinds.IsValid(row.Kind?.Value) ? row.Kind!.Value : LogicElementKinds.Contact;
        string relay = row.Relay.Trim().ToUpperInvariant();
        var element = new LogicElementDto
        {
            Id = row.ElementId,
            Kind = kind,
            Text = row.Text.Trim(),
            Severity = LogicElementKinds.IsCoil(kind)
                ? LogicConditionSeverities.Block
                : ValidSeverity(row.Severity?.Value)
        };
        if (string.Equals(kind, LogicElementKinds.Contact, StringComparison.OrdinalIgnoreCase))
        {
            element.Relay = relay;
            element.Op = ValidOp(row.Op?.Value);
            if (relay.Length == 0)
            {
                element.SourceId = row.SourceId;
                element.ItemId = row.ItemId;
                element.Value = NumericValue(element.Op, row.ValueText);
            }
        }
        else if (LogicElementKinds.IsCoil(kind))
        {
            element.Relay = relay;
        }
        else if (LogicElementKinds.IsTimer(kind))
        {
            element.PtMs = Milliseconds(row.PtSeconds);
        }
        else if (LogicElementKinds.IsCounter(kind))
        {
            element.Pv = int.TryParse(row.PvText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int preset)
                && preset > 0
                    ? preset
                    : 0;
        }

        return element;
    }

    /// <summary>
    /// The row list as the nested network: a row's inputs are the rows under it at a deeper indent,
    /// and the outermost rows are the block's roots.
    /// </summary>
    public static List<LogicElementDto> ElementTreeFromRows(IReadOnlyList<LogicElementRowViewModel> rows)
    {
        int position = 0;

        List<LogicElementDto> Build(int parentIndent)
        {
            var list = new List<LogicElementDto>();
            while (position < rows.Count && rows[position].Indent > parentIndent)
            {
                LogicElementRowViewModel node = rows[position++];
                LogicElementDto element = ElementFromRow(node);
                element.Inputs = Build(node.Indent);
                list.Add(element);
            }

            return list;
        }

        return Build(-1);
    }

    public static LogicStepDto StepFromRow(LogicStepRowViewModel row) => new()
    {
        Id = row.StepId,
        Name = row.Name.Trim(),
        Elements = new List<LogicElementDto>(),
        Conditions = row.Conditions.Select(ConditionFromRow).ToList(),
        NextStepText = TrimmedOrNull(row.NextStepText),
        CompletionSourceId = row.CompletionSourceId,
        CompletionItemId = row.CompletionItemId,
        Actions = row.StoredActions.ToList()
    };

    public static LogicActionDto ActionFromRow(LogicActionRowViewModel row) => new()
    {
        Label = row.Label.Trim(),
        SourceId = row.SourceId,
        ItemId = row.ItemId,
        Value = row.Value.Trim(),
        Confirm = row.Confirm
    };

    /// <summary>Builds the block exactly as the editor stands, with the web app's collect rules.</summary>
    public static LogicBlockDto Build(
        LogicBlockDto? stored,
        int newOrder,
        string name,
        string kind,
        bool enabled,
        string group,
        string description,
        string tagsText,
        IReadOnlyList<LogicConditionRowViewModel> conditions,
        IReadOnlyList<LogicElementRowViewModel> elements,
        IReadOnlyList<LogicStepRowViewModel> steps,
        IReadOnlyList<LogicActionRowViewModel> actions)
    {
        string effectiveKind = LogicBlockKinds.IsValid(kind) ? kind : LogicBlockKinds.Interlock;
        var block = new LogicBlockDto
        {
            Id = stored?.Id ?? Guid.Empty,
            Name = name.Trim(),
            Kind = effectiveKind,
            Enabled = enabled,
            Description = description.Trim(),
            Group = group.Trim(),
            Order = stored?.Order ?? newOrder,
            Tags = ParseTags(tagsText),
            Actions = actions.Select(ActionFromRow).ToList()
        };

        if (string.Equals(effectiveKind, LogicBlockKinds.Sequence, StringComparison.OrdinalIgnoreCase))
        {
            block.Steps = steps.Select(StepFromRow).ToList();
        }
        else if (elements.Count > 0)
        {
            block.Elements = ElementTreeFromRows(elements);
        }
        else
        {
            block.Conditions = conditions.Select(ConditionFromRow).ToList();
        }

        return block;
    }

    /// <summary>True when the built block is the stored one unchanged (Save greys out again).</summary>
    public static bool Matches(LogicBlockDto built, LogicBlockDto stored) =>
        JsonSerializer.Serialize(built) == JsonSerializer.Serialize(stored);

    private static string ValidOp(string? op) => LogicConditionOps.IsValid(op) ? op! : LogicConditionOps.On;

    private static string ValidSeverity(string? severity) =>
        LogicConditionSeverities.IsValid(severity) ? severity! : LogicConditionSeverities.Block;

    private static double? NumericValue(string? op, string text) =>
        LogicConditionOps.RequiresValue(op)
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;

    private static int Milliseconds(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0
            ? (int)Math.Round(seconds * 1000)
            : 0;

    private static string? TrimmedOrNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
