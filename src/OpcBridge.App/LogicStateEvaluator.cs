using System.Globalization;
using System.Text;
using System.Text.Json;
using OpcBridge.Client;
using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Derives the live state of every logic block from the bridge's tag values, walking the
/// block's IEC 61131-3 network. Pure and host-free — the API endpoint and the HMI broadcaster
/// both feed it the value/mapping lookups, so the derivation stays unit-testable without a
/// running bridge (function blocks need a <see cref="LogicStateStore"/>; without one a timer
/// passes its input straight through and a latch holds nothing).
///
/// Elements are three-valued: a contact is unknown when its tag has no value or bad quality,
/// true/false otherwise (on/off use the bridge's digital coercion, so Boolean and Byte 0/1 tags
/// both work), and a gate is unknown while its inputs leave the answer open. A warn element is
/// reported but contributes true to its parent — it can never block. Every element keeps the
/// reading it must show (<see cref="LogicElementStateDto.Should"/>) and whether the live value
/// satisfies it (<see cref="LogicElementStateDto.Matches"/>), which is what the phone renders
/// as "should 1 / actual 0"; a timer or counter reports its progress alongside.
/// </summary>
public static class LogicStateEvaluator
{
    private const double EqualityTolerance = 1e-9;

    /// <summary>Case-insensitive mapping index keyed by <see cref="Key"/>.</summary>
    public static IReadOnlyDictionary<string, TagMapping> BuildMappingIndex(IEnumerable<TagMapping> mappings)
    {
        Dictionary<string, TagMapping> index = new(StringComparer.OrdinalIgnoreCase);
        foreach (TagMapping mapping in mappings)
        {
            index[Key(mapping.SourceId, mapping.ItemId)] = mapping;
        }

        return index;
    }

    public static string Key(string? sourceId, string? itemId) =>
        string.Concat(sourceId?.Trim(), "::", itemId?.Trim());

    public static LogicStateSnapshot Evaluate(
        IEnumerable<LogicBlockDto> blocks,
        long version,
        Func<string, string, BridgeValueSnapshot?> valueLookup,
        Func<string, string, TagMapping?> mappingLookup,
        DateTime nowUtc,
        LogicStateStore? states = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        states?.BeginPass();
        try
        {
            LogicStateSnapshot snapshot = new()
            {
                Version = version,
                EvaluatedUtc = nowUtc
            };

            foreach (LogicBlockDto block in blocks.OrderBy(block => block.Order))
            {
                snapshot.Blocks.Add(EvaluateBlock(block, valueLookup, mappingLookup, states, nowUtc));
            }

            return snapshot;
        }
        finally
        {
            states?.EndPass();
        }
    }

    public static LogicBlockStateDto EvaluateBlock(
        LogicBlockDto block,
        Func<string, string, BridgeValueSnapshot?> valueLookup,
        Func<string, string, TagMapping?> mappingLookup,
        LogicStateStore? states = null,
        DateTime nowUtc = default)
    {
        ArgumentNullException.ThrowIfNull(block);

        List<LogicElementStateDto> elements = new();
        List<LogicStepStateDto> steps = new();

        string state;
        string? reason;

        if (!block.Enabled)
        {
            EvaluateNetwork(LogicNetwork.For(block), valueLookup, mappingLookup, states, nowUtc, elements);
            foreach (LogicStepDto step in block.Steps)
            {
                EvaluateNetwork(LogicNetwork.For(step), valueLookup, mappingLookup, states, nowUtc, elements);
            }

            state = LogicBlockStates.Disabled;
            reason = "block is disabled";
        }
        else if (string.Equals(block.Kind, LogicBlockKinds.Sequence, StringComparison.OrdinalIgnoreCase))
        {
            (state, reason) = EvaluateSequence(block, valueLookup, mappingLookup, states, nowUtc, elements, steps);
        }
        else
        {
            (bool? output, string? failing, string? missing) =
                EvaluateNetwork(LogicNetwork.For(block), valueLookup, mappingLookup, states, nowUtc, elements);
            (state, reason) = BlockVerdict(output, failing, missing);
        }

        return new LogicBlockStateDto
        {
            Id = block.Id,
            State = state,
            Reason = reason,
            Elements = elements,
            Steps = steps
        };
    }

    /// <summary>
    /// A cheap fingerprint of the derived states (no value text — that follows the tag
    /// stream), so the broadcaster can push only when something actually changed. A running
    /// timer advances by the whole second and a counter by its value, so progress is reported
    /// without pushing on every tick.
    /// </summary>
    public static string Signature(LogicStateSnapshot snapshot)
    {
        StringBuilder builder = new();
        foreach (LogicBlockStateDto block in snapshot.Blocks)
        {
            builder.Append(block.Id).Append(':').Append(block.State).Append(':').Append(block.Reason).Append('|');

            foreach (LogicElementStateDto element in block.Elements)
            {
                builder.Append(element.Id).Append(':').Append(element.State).Append(':')
                    .Append(element.Matches switch { true => '1', false => '0', null => '?' }).Append(':')
                    .Append(element.PtMs > 0 ? (element.ElapsedMs / 1000).ToString(CultureInfo.InvariantCulture) : string.Empty).Append(':')
                    .Append(element.Count != 0 ? element.Count.ToString(CultureInfo.InvariantCulture) : string.Empty)
                    .Append('|');
            }

            foreach (LogicStepStateDto step in block.Steps)
            {
                builder.Append(step.Id).Append(':').Append(step.State).Append(':').Append(step.Reason).Append('|');
            }
        }

        return builder.ToString();
    }

    private static (string State, string? Reason) EvaluateSequence(
        LogicBlockDto block,
        Func<string, string, BridgeValueSnapshot?> valueLookup,
        Func<string, string, TagMapping?> mappingLookup,
        LogicStateStore? states,
        DateTime nowUtc,
        List<LogicElementStateDto> elements,
        List<LogicStepStateDto> steps)
    {
        bool foundCurrent = false;
        LogicStepStateDto? current = null;
        bool allDone = block.Steps.Count > 0;

        foreach (LogicStepDto step in block.Steps)
        {
            (bool? output, string? failing, string? missing) =
                EvaluateNetwork(LogicNetwork.For(step), valueLookup, mappingLookup, states, nowUtc, elements);
            bool stepDone = output == true;

            bool completionConfigured = !string.IsNullOrWhiteSpace(step.CompletionSourceId)
                && !string.IsNullOrWhiteSpace(step.CompletionItemId);
            bool completionUnknown = false;
            string? completionText = null;
            if (stepDone && completionConfigured)
            {
                BridgeValueSnapshot? completion = valueLookup(step.CompletionSourceId!, step.CompletionItemId!);
                if (completion is null || !completion.IsGood)
                {
                    stepDone = false;
                    completionUnknown = true;
                    completionText = "no data for " + Label(step.CompletionSourceId, step.CompletionItemId, mappingLookup);
                }
                else if (!TagDigital.CoerceBool(completion.Value))
                {
                    stepDone = false;
                    completionText = "waiting for " + Label(step.CompletionSourceId, step.CompletionItemId, mappingLookup);
                }
            }

            string stepState;
            string? stepReason = null;
            if (!foundCurrent)
            {
                if (stepDone)
                {
                    stepState = LogicStepStates.Done;
                }
                else
                {
                    // The first step that is not done is where the sequence stands; everything
                    // after it stays pending even when its own network already happens to be true.
                    foundCurrent = true;
                    allDone = false;
                    bool dataMissing = missing is not null || completionUnknown || output is null;
                    stepState = dataMissing ? LogicStepStates.Unknown : LogicStepStates.Current;
                    stepReason = failing
                        ?? (missing is not null ? "no data for " + missing : null)
                        ?? completionText
                        ?? "step is not done";
                }
            }
            else
            {
                stepState = LogicStepStates.Pending;
            }

            LogicStepStateDto stepStateDto = new()
            {
                Id = step.Id,
                State = stepState,
                Reason = stepReason
            };
            steps.Add(stepStateDto);

            if (stepState is LogicStepStates.Current or LogicStepStates.Unknown)
            {
                current = stepStateDto;
            }
        }

        if (allDone)
        {
            return (LogicBlockStates.Ready, null);
        }

        if (current is not null && current.State == LogicStepStates.Unknown)
        {
            return (LogicBlockStates.Unknown, current.Reason);
        }

        return (LogicBlockStates.Blocked, current?.Reason);
    }

    /// <summary>
    /// Evaluates one network in drawing order (a parent before its inputs), appending every
    /// element's state to <paramref name="target"/>. Returns the combined output — the roots
    /// AND'ed together — plus the reason of the first root that is not satisfied, which becomes
    /// the block's reason.
    /// </summary>
    private static (bool? Output, string? Failing, string? Missing) EvaluateNetwork(
        IReadOnlyList<LogicElementDto> roots,
        Func<string, string, BridgeValueSnapshot?> valueLookup,
        Func<string, string, TagMapping?> mappingLookup,
        LogicStateStore? states,
        DateTime nowUtc,
        List<LogicElementStateDto> target)
    {
        List<bool?> outputs = new();
        string? failing = null;
        string? missing = null;

        foreach (LogicElementDto root in roots)
        {
            bool? output = EvaluateElement(root, 0, valueLookup, mappingLookup, states, nowUtc, target, out string? reason, out string? missingLeaf);
            if (IsWarn(root))
            {
                // A warn element at the root is reported but never gates the block.
                continue;
            }

            outputs.Add(output);
            if (output == false && failing is null)
            {
                failing = reason ?? ElementLabel(root);
            }
            else if (output is null && missing is null)
            {
                missing = missingLeaf ?? ElementLabel(root);
            }
        }

        return (Combine.And(outputs), failing, missing);
    }

    /// <summary>
    /// Evaluates one element and reports, alongside its output, why it is not satisfied —
    /// <paramref name="reason"/> when it is false and <paramref name="missing"/> when it is
    /// unknown. A gate passes on the cause of the input that decided it, so the reason names
    /// what actually failed rather than the first thing the walk happened to see.
    /// </summary>
    private static bool? EvaluateElement(
        LogicElementDto element,
        int depth,
        Func<string, string, BridgeValueSnapshot?> valueLookup,
        Func<string, string, TagMapping?> mappingLookup,
        LogicStateStore? states,
        DateTime nowUtc,
        List<LogicElementStateDto> target,
        out string? reason,
        out string? missing)
    {
        LogicElementStateDto row = new()
        {
            Id = element.Id,
            Kind = element.Kind.ToLowerInvariant(),
            Depth = depth,
            State = LogicConditionStates.Unknown
        };
        target.Add(row);

        List<bool?> inputs = new();
        List<string?> inputReasons = new();
        List<string?> inputMissing = new();
        foreach (LogicElementDto input in element.Inputs)
        {
            bool? value = EvaluateElement(input, depth + 1, valueLookup, mappingLookup, states, nowUtc, target, out string? inputReason, out string? inputMissingLeaf);
            // A warn element is reported but inert: it neither satisfies nor blocks its parent.
            if (!IsWarn(input))
            {
                inputs.Add(value);
                inputReasons.Add(inputReason);
                inputMissing.Add(inputMissingLeaf);
            }
        }

        string kind = element.Kind.ToLowerInvariant();
        bool? output;
        if (kind == LogicElementKinds.Contact)
        {
            output = EvaluateContact(element, row, valueLookup, mappingLookup);
        }
        else
        {
            output = EvaluateBlockElement(element, kind, inputs, row, states, nowUtc);
        }

        row.State = StateOf(output);
        string own = ElementLabel(element);
        reason = null;
        missing = null;
        if (!IsWarn(element))
        {
            if (kind == LogicElementKinds.Contact)
            {
                reason = output == false ? own : null;
                missing = output is null ? LeafLabel(element, mappingLookup) : null;
            }
            else if (LogicElementKinds.IsGate(kind))
            {
                // A gate the author labelled names itself when it is false; a bare one points at
                // the input that decided it. A missing reading always points at the tag.
                bool named = !string.IsNullOrWhiteSpace(element.Text);
                reason = output == false ? (named ? own : FirstCause(inputs, inputReasons, false) ?? own) : null;
                missing = output is null ? FirstCause(inputs, inputMissing, null) ?? own : null;
            }
            else
            {
                // A function block is its own cause: the time has not elapsed, the count is not
                // reached, the latch is unset.
                reason = output == false ? own : null;
                missing = output is null ? own : null;
            }
        }

        return output;
    }

    /// <summary>The reason of the first input whose value is <paramref name="state"/> (false or unknown).</summary>
    private static string? FirstCause(IReadOnlyList<bool?> inputs, IReadOnlyList<string?> causes, bool? state)
    {
        for (int i = 0; i < inputs.Count && i < causes.Count; i++)
        {
            if (inputs[i] == state && !string.IsNullOrWhiteSpace(causes[i]))
            {
                return causes[i];
            }
        }

        return null;
    }

    private static bool? EvaluateContact(
        LogicElementDto element,
        LogicElementStateDto row,
        Func<string, string, BridgeValueSnapshot?> valueLookup,
        Func<string, string, TagMapping?> mappingLookup)
    {
        BridgeValueSnapshot? snapshot = valueLookup(element.SourceId, element.ItemId);
        TagMapping? mapping = mappingLookup(element.SourceId, element.ItemId);
        string valueText = FormatValueText(snapshot?.Value, mapping);

        row.Should = ShouldText(element, mapping);
        if (snapshot is null || !snapshot.IsGood)
        {
            row.ValueText = valueText;
            return null;
        }

        bool? matches = Compare(element, snapshot.Value, mapping);
        row.Matches = matches;
        row.ValueText = matches is null
            ? (valueText == "—" ? "not numeric" : valueText)
            : valueText;
        row.TimestampUtc = snapshot.TimestampUtc;
        return matches;
    }

    private static bool? EvaluateBlockElement(
        LogicElementDto element,
        string kind,
        List<bool?> inputs,
        LogicElementStateDto row,
        LogicStateStore? states,
        DateTime nowUtc)
    {
        bool? first = inputs.Count > 0 ? inputs[0] : null;
        switch (kind)
        {
            case LogicElementKinds.And:
                return Combine.And(inputs);
            case LogicElementKinds.Or:
                return Combine.Or(inputs);
            case LogicElementKinds.Xor:
                return Combine.Xor(inputs);
            case LogicElementKinds.Not:
                return first is null ? null : !first;

            case LogicElementKinds.Ton:
            {
                row.PtMs = element.PtMs;
                if (states is null || element.PtMs <= 0)
                {
                    row.ElapsedMs = 0;
                    return first;
                }

                (bool satisfied, int elapsed) = states.Ton(element.Id, first == true, element.PtMs, nowUtc);
                row.ElapsedMs = elapsed;
                return first is null ? null : satisfied;
            }

            case LogicElementKinds.Tof:
            {
                row.PtMs = element.PtMs;
                if (states is null || element.PtMs <= 0)
                {
                    row.ElapsedMs = 0;
                    return first;
                }

                (bool satisfied, int elapsed) = states.Tof(element.Id, first == true, element.PtMs, nowUtc);
                row.ElapsedMs = elapsed;
                return first is null ? null : satisfied;
            }

            case LogicElementKinds.Tp:
            {
                row.PtMs = element.PtMs;
                if (states is null || element.PtMs <= 0)
                {
                    row.ElapsedMs = 0;
                    return first;
                }

                (bool satisfied, int elapsed) = states.Tp(element.Id, first == true, element.PtMs, nowUtc);
                row.ElapsedMs = elapsed;
                return first is null ? null : satisfied;
            }

            case LogicElementKinds.Ctu:
            {
                bool? up = first;
                bool? reset = inputs.Count > 1 ? inputs[1] : false;
                if (states is null)
                {
                    return up == true && element.Pv <= 0;
                }

                (bool reached, int count) = states.Ctu(element.Id, up == true, reset == true, element.Pv);
                row.Count = count;
                return up is null || reset is null ? null : reached;
            }

            case LogicElementKinds.Ctd:
            {
                bool? down = first;
                bool? load = inputs.Count > 1 ? inputs[1] : false;
                if (states is null)
                {
                    return element.Pv <= 0;
                }

                (bool reached, int count) = states.Ctd(element.Id, down == true, load == true, element.Pv);
                row.Count = count;
                return down is null || load is null ? null : reached;
            }

            case LogicElementKinds.Sr:
            case LogicElementKinds.Rs:
            {
                bool? set = first;
                bool? reset = inputs.Count > 1 ? inputs[1] : false;
                if (states is null)
                {
                    return set;
                }

                bool latched = kind == LogicElementKinds.Sr
                    ? states.Sr(element.Id, set == true, reset == true)
                    : states.Rs(element.Id, set == true, reset == true);
                return set is null || reset is null ? null : latched;
            }

            case LogicElementKinds.RisingEdge:
                return states is null || first is null ? first == true : states.Edge(element.Id, first == true, rising: true);

            case LogicElementKinds.FallingEdge:
                return states is null || first is null ? first == false : states.Edge(element.Id, first == true, rising: false);

            default:
                return null;
        }
    }

    private static (string State, string? Reason) BlockVerdict(bool? output, string? failing, string? missing)
    {
        if (output == true)
        {
            return (LogicBlockStates.Ready, null);
        }

        if (output is null || (failing is null && missing is not null))
        {
            return (LogicBlockStates.Unknown, "no data for " + (missing ?? "(unmapped)"));
        }

        return (LogicBlockStates.Blocked, failing);
    }

    private static string StateOf(bool? value) => value switch
    {
        true => LogicConditionStates.True,
        false => LogicConditionStates.False,
        _ => LogicConditionStates.Unknown
    };

    private static bool IsWarn(LogicElementDto element) =>
        string.Equals(element.Severity, LogicConditionSeverities.Warn, StringComparison.OrdinalIgnoreCase);

    /// <summary>The element's own words for a reason line: its sentence, else its kind.</summary>
    private static string ElementLabel(LogicElementDto element) =>
        string.IsNullOrWhiteSpace(element.Text) ? KindLabel(element.Kind) : element.Text;

    /// <summary>What a missing reading names: the tag for a contact, else the element's sentence.</summary>
    private static string LeafLabel(LogicElementDto element, Func<string, string, TagMapping?> mappingLookup) =>
        string.Equals(element.Kind, LogicElementKinds.Contact, StringComparison.OrdinalIgnoreCase)
            ? Label(element.SourceId, element.ItemId, mappingLookup)
            : ElementLabel(element);

    private static string KindLabel(string? kind) => (kind ?? string.Empty).ToLowerInvariant() switch
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
        _ => "element"
    };

    /// <summary>
    /// The reading a contact must show: "1" for a normally open contact (<c>on</c>), "0" for a
    /// normally closed one (<c>off</c>), or the comparison itself (e.g. "&gt; 50").
    /// </summary>
    private static string ShouldText(LogicElementDto element, TagMapping? mapping)
    {
        string op = element.Op.ToLowerInvariant();
        return op switch
        {
            LogicConditionOps.On => "1",
            LogicConditionOps.Off => "0",
            LogicConditionOps.GreaterThan => "> " + FormatNumber(element.Value ?? 0, mapping?.Decimals),
            LogicConditionOps.LessThan => "< " + FormatNumber(element.Value ?? 0, mapping?.Decimals),
            LogicConditionOps.Equal => "= " + FormatNumber(element.Value ?? 0, mapping?.Decimals),
            _ => string.Empty
        };
    }

    private static bool? Compare(LogicElementDto element, object? value, TagMapping? mapping)
    {
        string op = element.Op.ToLowerInvariant();
        if (op is LogicConditionOps.On or LogicConditionOps.Off)
        {
            bool on = TagDigital.CoerceBool(value);
            return op == LogicConditionOps.On ? on : !on;
        }

        if (element.Value is not double target || !TryNumber(value, out double number))
        {
            return null;
        }

        return op switch
        {
            LogicConditionOps.GreaterThan => number > target,
            LogicConditionOps.LessThan => number < target,
            LogicConditionOps.Equal => Math.Abs(number - target) <= EqualityTolerance * Math.Max(1.0, Math.Abs(target)),
            _ => null
        };
    }

    private static string Label(string? sourceId, string? itemId, Func<string, string, TagMapping?> mappingLookup)
    {
        TagMapping? mapping = mappingLookup(sourceId ?? string.Empty, itemId ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(mapping?.DisplayName))
        {
            return mapping!.DisplayName;
        }

        return string.IsNullOrWhiteSpace(itemId) ? "(unmapped)" : itemId;
    }

    private static string FormatValueText(object? value, TagMapping? mapping)
    {
        if (value is null)
        {
            return "—";
        }

        if (value is JsonElement element)
        {
            value = element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => element.TryGetDouble(out double elementNumber) ? elementNumber : element.ToString(),
                JsonValueKind.String => element.GetString(),
                _ => null
            };

            if (value is null)
            {
                return "—";
            }
        }

        if (TagDigital.Resolve(mapping) || value is bool)
        {
            bool on = TagDigital.CoerceBool(value);
            string? text = on ? mapping?.OnText : mapping?.OffText;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text!;
            }

            return on ? "On" : "Off";
        }

        if (TryNumber(value, out double number))
        {
            string numberText = FormatNumber(number, mapping?.Decimals);
            string? unit = mapping?.Unit;
            return string.IsNullOrWhiteSpace(unit) ? numberText : numberText + " " + unit;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—";
    }

    private static string FormatNumber(double number, int? decimals)
    {
        if (decimals is >= 0 and <= 15)
        {
            return Math.Round(number, decimals.Value, MidpointRounding.AwayFromZero)
                .ToString("F" + decimals.Value, CultureInfo.InvariantCulture);
        }

        return number.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case byte by:
                number = by;
                return true;
            case sbyte sb:
                number = sb;
                return true;
            case short s:
                number = s;
                return true;
            case ushort us:
                number = us;
                return true;
            case int i:
                number = i;
                return true;
            case uint ui:
                number = ui;
                return true;
            case long l:
                number = l;
                return true;
            case ulong ul:
                number = ul;
                return true;
            case float f:
                number = f;
                return true;
            case double d:
                number = d;
                return true;
            case decimal m:
                number = (double)m;
                return true;
            case bool b:
                number = b ? 1 : 0;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number:
                return element.TryGetDouble(out number);
            case JsonElement element when element.ValueKind is JsonValueKind.True or JsonValueKind.False:
                number = element.GetBoolean() ? 1 : 0;
                return true;
            case string s:
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            default:
                number = 0;
                return false;
        }
    }

    /// <summary>
    /// Three-valued boolean combining (Kleene logic): an answer is known only when the inputs
    /// settle it, so a gate over a tag with no value reads unknown rather than inventing a state.
    /// </summary>
    private static class Combine
    {
        public static bool? And(IReadOnlyList<bool?> inputs)
        {
            if (inputs.Count == 0)
            {
                return true;
            }

            if (inputs.Any(input => input == false))
            {
                return false;
            }

            return inputs.All(input => input == true) ? true : null;
        }

        public static bool? Or(IReadOnlyList<bool?> inputs)
        {
            if (inputs.Any(input => input == true))
            {
                return true;
            }

            return inputs.All(input => input == false) ? false : null;
        }

        /// <summary>Parity: true when an odd number of inputs is true (the standard XOR over two).</summary>
        public static bool? Xor(IReadOnlyList<bool?> inputs)
        {
            if (inputs.Any(input => input is null))
            {
                // An unknown input can still flip the parity either way.
                return null;
            }

            return inputs.Count(input => input == true) % 2 == 1;
        }
    }
}
