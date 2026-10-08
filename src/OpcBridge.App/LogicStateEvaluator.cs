using System.Globalization;
using System.Text;
using System.Text.Json;
using OpcBridge.Client;
using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Derives the live state of every logic block from the bridge's tag values. Pure and
/// host-free — the API endpoint and the HMI broadcaster both feed it the value/mapping
/// lookups, so the derivation stays unit-testable without a running bridge.
///
/// Condition resolution: a condition is unknown when its tag has no value or bad quality,
/// true/false otherwise (on/off use the bridge's digital coercion, so Boolean and Byte 0/1
/// tags both work). Interlock/permissive blocks are ready when every block-severity
/// condition is true; a sequence walks its steps in order and the first step that is not
/// done becomes the current one whose failing part is the block's reason.
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
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        LogicStateSnapshot snapshot = new()
        {
            Version = version,
            EvaluatedUtc = nowUtc
        };

        foreach (LogicBlockDto block in blocks.OrderBy(block => block.Order))
        {
            snapshot.Blocks.Add(EvaluateBlock(block, valueLookup, mappingLookup));
        }

        return snapshot;
    }

    public static LogicBlockStateDto EvaluateBlock(
        LogicBlockDto block,
        Func<string, string, BridgeValueSnapshot?> valueLookup,
        Func<string, string, TagMapping?> mappingLookup)
    {
        ArgumentNullException.ThrowIfNull(block);

        List<LogicConditionStateDto> conditions = new();
        List<LogicStepStateDto> steps = new();

        string state;
        string? reason;

        if (!block.Enabled)
        {
            EvaluateConditions(block.Conditions, valueLookup, mappingLookup, conditions);
            EvaluateStepConditions(block.Steps, valueLookup, mappingLookup, conditions);
            state = LogicBlockStates.Disabled;
            reason = "block is disabled";
        }
        else if (string.Equals(block.Kind, LogicBlockKinds.Sequence, StringComparison.OrdinalIgnoreCase))
        {
            (state, reason) = EvaluateSequence(block, valueLookup, mappingLookup, conditions, steps);
        }
        else
        {
            (state, reason) = EvaluateFlat(block, valueLookup, mappingLookup, conditions);
        }

        return new LogicBlockStateDto
        {
            Id = block.Id,
            State = state,
            Reason = reason,
            Conditions = conditions,
            Steps = steps
        };
    }

    /// <summary>
    /// A cheap fingerprint of the derived states (no value text — that follows the tag
    /// stream), so the broadcaster can push only when something actually changed.
    /// </summary>
    public static string Signature(LogicStateSnapshot snapshot)
    {
        StringBuilder builder = new();
        foreach (LogicBlockStateDto block in snapshot.Blocks)
        {
            builder.Append(block.Id).Append(':').Append(block.State).Append(':').Append(block.Reason).Append('|');
            foreach (LogicConditionStateDto condition in block.Conditions)
            {
                builder.Append(condition.Id).Append(':').Append(condition.State).Append('|');
            }

            foreach (LogicStepStateDto step in block.Steps)
            {
                builder.Append(step.Id).Append(':').Append(step.State).Append(':').Append(step.Reason).Append('|');
            }
        }

        return builder.ToString();
    }

    private static (string State, string? Reason) EvaluateFlat(
        LogicBlockDto block,
        Func<string, string, BridgeValueSnapshot?> valueLookup,
        Func<string, string, TagMapping?> mappingLookup,
        List<LogicConditionStateDto> conditions)
    {
        EvaluateConditions(block.Conditions, valueLookup, mappingLookup, conditions);

        string? firstFalseText = null;
        string? firstUnknownLabel = null;

        for (int i = 0; i < block.Conditions.Count; i++)
        {
            LogicConditionDto condition = block.Conditions[i];
            if (string.Equals(condition.Severity, LogicConditionSeverities.Warn, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            LogicConditionStateDto state = conditions[i];
            if (state.State == LogicConditionStates.False && firstFalseText is null)
            {
                firstFalseText = condition.Text;
            }
            else if (state.State == LogicConditionStates.Unknown && firstUnknownLabel is null)
            {
                firstUnknownLabel = ConditionLabel(condition, mappingLookup);
            }
        }

        if (firstFalseText is not null)
        {
            return (LogicBlockStates.Blocked, firstFalseText);
        }

        if (firstUnknownLabel is not null)
        {
            return (LogicBlockStates.Unknown, "no data for " + firstUnknownLabel);
        }

        return (LogicBlockStates.Ready, null);
    }

    private static (string State, string? Reason) EvaluateSequence(
        LogicBlockDto block,
        Func<string, string, BridgeValueSnapshot?> valueLookup,
        Func<string, string, TagMapping?> mappingLookup,
        List<LogicConditionStateDto> conditions,
        List<LogicStepStateDto> steps)
    {
        bool foundCurrent = false;
        LogicStepStateDto? current = null;
        bool allDone = block.Steps.Count > 0;

        foreach (LogicStepDto step in block.Steps)
        {
            List<LogicConditionStateDto> stepConditions = new();
            EvaluateConditions(step.Conditions, valueLookup, mappingLookup, stepConditions);
            conditions.AddRange(stepConditions);

            string? firstFalseText = null;
            string? firstUnknownLabel = null;
            for (int i = 0; i < step.Conditions.Count; i++)
            {
                LogicConditionDto condition = step.Conditions[i];
                if (string.Equals(condition.Severity, LogicConditionSeverities.Warn, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                LogicConditionStateDto conditionState = stepConditions[i];
                if (conditionState.State == LogicConditionStates.False && firstFalseText is null)
                {
                    firstFalseText = condition.Text;
                }
                else if (conditionState.State == LogicConditionStates.Unknown && firstUnknownLabel is null)
                {
                    firstUnknownLabel = ConditionLabel(condition, mappingLookup);
                }
            }

            bool completionConfigured = !string.IsNullOrWhiteSpace(step.CompletionSourceId)
                && !string.IsNullOrWhiteSpace(step.CompletionItemId);
            bool completionTrue = true;
            bool completionUnknown = false;
            string? completionText = null;
            if (completionConfigured)
            {
                BridgeValueSnapshot? completion = valueLookup(step.CompletionSourceId!, step.CompletionItemId!);
                if (completion is null || !completion.IsGood)
                {
                    completionTrue = false;
                    completionUnknown = true;
                    completionText = "no data for " + Label(step.CompletionSourceId, step.CompletionItemId, mappingLookup);
                }
                else if (!TagDigital.CoerceBool(completion.Value))
                {
                    completionTrue = false;
                    completionText = "waiting for " + Label(step.CompletionSourceId, step.CompletionItemId, mappingLookup);
                }
            }

            bool stepDone = firstFalseText is null && firstUnknownLabel is null && completionTrue;

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
                    // after it stays pending even when its own conditions already happen to be true.
                    foundCurrent = true;
                    allDone = false;
                    bool dataMissing = firstUnknownLabel is not null || completionUnknown;
                    stepState = dataMissing ? LogicStepStates.Unknown : LogicStepStates.Current;
                    stepReason = firstFalseText
                        ?? (firstUnknownLabel is not null ? "no data for " + firstUnknownLabel : null)
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

            if (stepState == LogicStepStates.Current || stepState == LogicStepStates.Unknown)
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

    private static void EvaluateConditions(
        List<LogicConditionDto> source,
        Func<string, string, BridgeValueSnapshot?> valueLookup,
        Func<string, string, TagMapping?> mappingLookup,
        List<LogicConditionStateDto> target)
    {
        foreach (LogicConditionDto condition in source)
        {
            BridgeValueSnapshot? snapshot = valueLookup(condition.SourceId, condition.ItemId);
            TagMapping? mapping = mappingLookup(condition.SourceId, condition.ItemId);
            string valueText = FormatValueText(snapshot?.Value, mapping);

            string state;
            if (snapshot is null || !snapshot.IsGood)
            {
                state = LogicConditionStates.Unknown;
            }
            else
            {
                bool? result = Compare(condition, snapshot.Value, mapping);
                state = result switch
                {
                    true => LogicConditionStates.True,
                    false => LogicConditionStates.False,
                    null => LogicConditionStates.Unknown
                };

                if (state == LogicConditionStates.Unknown)
                {
                    valueText = valueText == "—" ? "not numeric" : valueText;
                }
            }

            target.Add(new LogicConditionStateDto
            {
                Id = condition.Id,
                State = state,
                ValueText = valueText,
                TimestampUtc = snapshot?.TimestampUtc
            });
        }
    }

    private static void EvaluateStepConditions(
        List<LogicStepDto> steps,
        Func<string, string, BridgeValueSnapshot?> valueLookup,
        Func<string, string, TagMapping?> mappingLookup,
        List<LogicConditionStateDto> target)
    {
        foreach (LogicStepDto step in steps)
        {
            EvaluateConditions(step.Conditions, valueLookup, mappingLookup, target);
        }
    }

    private static bool? Compare(LogicConditionDto condition, object? value, TagMapping? mapping)
    {
        string op = condition.Op.ToLowerInvariant();
        if (op is LogicConditionOps.On or LogicConditionOps.Off)
        {
            bool on = TagDigital.CoerceBool(value);
            return op == LogicConditionOps.On ? on : !on;
        }

        if (condition.Value is not double target || !TryNumber(value, out double number))
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

    private static string ConditionLabel(LogicConditionDto condition, Func<string, string, TagMapping?> mappingLookup) =>
        Label(condition.SourceId, condition.ItemId, mappingLookup);

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
}
