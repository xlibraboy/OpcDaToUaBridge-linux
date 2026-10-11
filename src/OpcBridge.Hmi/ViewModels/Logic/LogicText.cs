using System.Globalization;
using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// Plant-logic wording, shared by the editor rows and the result view. The strings follow the
/// phone word for word — state words, "should 1 · actual 0", timer and counter progress — so the
/// plant reads the same thing on every surface.
/// </summary>
public static class LogicText
{
    public static string BlockKindLabel(string? kind) => kind?.ToLowerInvariant() switch
    {
        LogicBlockKinds.Sequence => "Sequence",
        LogicBlockKinds.Permissive => "Permissive",
        _ => "Interlock"
    };

    /// <summary>The block state word, always shown next to the colour.</summary>
    public static string StateWord(string? state) => state?.ToLowerInvariant() switch
    {
        LogicBlockStates.Ready => "READY",
        LogicBlockStates.Blocked => "BLOCKED",
        LogicBlockStates.Disabled => "DISABLED",
        _ => "NO DATA"
    };

    public static string StepStateWord(string? state) => state?.ToLowerInvariant() switch
    {
        LogicStepStates.Done => "Done",
        LogicStepStates.Current => "Current",
        LogicStepStates.Pending => "Pending",
        _ => "No data"
    };

    /// <summary>Colour role for a block/step state: ok, bad, muted or warn — never colour alone.</summary>
    public static string StateKey(string? state) => state?.ToLowerInvariant() switch
    {
        LogicBlockStates.Ready => "ok",
        LogicBlockStates.Blocked => "bad",
        LogicBlockStates.Disabled => "muted",
        _ => "warn"
    };

    /// <summary>Colour role for a condition's three-valued state.</summary>
    public static string ConditionStateKey(string? state) => state?.ToLowerInvariant() switch
    {
        LogicConditionStates.True => "ok",
        LogicConditionStates.False => "bad",
        _ => "warn"
    };

    public static string Mark(string? state) => state?.ToLowerInvariant() switch
    {
        LogicConditionStates.True => "✓",
        LogicConditionStates.False => "✗",
        _ => "—"
    };

    /// <summary>Result row kind label: NO / NC / &gt; for contacts, the standard block names otherwise.</summary>
    public static string ElementKindLabel(string? kind, string? op)
    {
        if (string.Equals(kind, LogicElementKinds.Contact, StringComparison.OrdinalIgnoreCase))
        {
            return op?.ToLowerInvariant() switch
            {
                LogicConditionOps.Off => "NC",
                LogicConditionOps.GreaterThan => ">",
                LogicConditionOps.LessThan => "<",
                LogicConditionOps.Equal => "=",
                _ => "NO"
            };
        }

        return kind?.ToLowerInvariant() switch
        {
            LogicElementKinds.And => "AND",
            LogicElementKinds.Or => "OR",
            LogicElementKinds.Xor => "XOR",
            LogicElementKinds.Not => "NOT",
            LogicElementKinds.Ton => "TON IN",
            LogicElementKinds.Tof => "TOF IN",
            LogicElementKinds.Tp => "TP IN",
            LogicElementKinds.Ctu => "CTU CU",
            LogicElementKinds.Ctd => "CTD CD",
            LogicElementKinds.Sr => "SR S1",
            LogicElementKinds.Rs => "RS S",
            LogicElementKinds.RisingEdge => "R_TRIG IN",
            LogicElementKinds.FallingEdge => "F_TRIG IN",
            _ => kind ?? LogicElementKinds.Contact
        };
    }

    /// <summary>
    /// What the element reads right now: 1/0 for a contact (flipped for a normally closed one),
    /// the live value for a comparison, the output bit for a gate / function block, ? when unknown.
    /// </summary>
    public static string Actual(LogicElementStateDto? state, string? kind, string? op)
    {
        if (state is null)
        {
            return "—";
        }

        if (string.Equals(kind, LogicElementKinds.Contact, StringComparison.OrdinalIgnoreCase))
        {
            string effectiveOp = string.IsNullOrEmpty(op)
                ? (string.Equals(state.Should, "0", StringComparison.Ordinal) ? LogicConditionOps.Off : LogicConditionOps.On)
                : op;
            if (string.Equals(effectiveOp, LogicConditionOps.On, StringComparison.OrdinalIgnoreCase)
                || string.Equals(effectiveOp, LogicConditionOps.Off, StringComparison.OrdinalIgnoreCase))
            {
                if (state.Matches is null)
                {
                    return "?";
                }

                bool bit = state.Matches == true;
                bool normallyOpen = string.Equals(effectiveOp, LogicConditionOps.On, StringComparison.OrdinalIgnoreCase);
                return normallyOpen == bit ? "1" : "0";
            }

            // A numeric condition keeps its value — a level above 50 % is not a bit.
            return string.IsNullOrEmpty(state.ValueText) || state.ValueText == "—" ? "?" : state.ValueText;
        }

        return state.State?.ToLowerInvariant() switch
        {
            LogicConditionStates.True => "1",
            LogicConditionStates.False => "0",
            _ => "—"
        };
    }

    /// <summary>"should X · actual Y" for a contact, empty for gates and function blocks.</summary>
    public static string ShouldLine(LogicElementStateDto? state, string? kind, string? op)
    {
        if (state is null || string.IsNullOrEmpty(state.Should))
        {
            return string.Empty;
        }

        return $"should {state.Should} · actual {Actual(state, kind, op)}";
    }

    /// <summary>"holding 1.5 s of 3 s", "off-delay done · 3 s", "count 2 of 5".</summary>
    public static string Progress(LogicElementStateDto? state, int presetPv)
    {
        if (state is null)
        {
            return string.Empty;
        }

        if (LogicElementKinds.IsTimer(state.Kind))
        {
            int preset = state.PtMs;
            if (preset <= 0)
            {
                return string.Empty;
            }

            string verb = state.Kind.ToLowerInvariant() switch
            {
                LogicElementKinds.Tof => "off-delay",
                LogicElementKinds.Tp => "pulse",
                _ => "holding"
            };
            return string.Equals(state.State, LogicConditionStates.True, StringComparison.OrdinalIgnoreCase)
                ? $"{verb} done · {Seconds(preset)}"
                : $"{verb} {Seconds(state.ElapsedMs)} of {Seconds(preset)}";
        }

        if (LogicElementKinds.IsCounter(state.Kind))
        {
            return $"count {state.Count} of {presetPv}";
        }

        return string.Empty;
    }

    /// <summary>
    /// The editor's live chip: a bit reads 1 / 0 (no data when unknown); a numeric condition keeps
    /// its live value.
    /// </summary>
    public static string LiveChip(LogicElementStateDto? state, string? op)
    {
        if (state is null)
        {
            return "—";
        }

        bool isBoolean = string.Equals(op, LogicConditionOps.On, StringComparison.OrdinalIgnoreCase)
            || string.Equals(op, LogicConditionOps.Off, StringComparison.OrdinalIgnoreCase);
        if (isBoolean)
        {
            return state.State?.ToLowerInvariant() switch
            {
                LogicConditionStates.True => "1",
                LogicConditionStates.False => "0",
                _ => "no data"
            };
        }

        return string.IsNullOrEmpty(state.ValueText) ? "no data" : state.ValueText;
    }

    /// <summary>The list/result reason line: "all conditions met", the first failing reason, "reading…".</summary>
    public static string ReasonLine(LogicBlockStateDto? state)
    {
        if (state is null)
        {
            return "reading…";
        }

        return string.Equals(state.State, LogicBlockStates.Ready, StringComparison.OrdinalIgnoreCase)
            ? "all conditions met"
            : state.Reason ?? string.Empty;
    }

    /// <summary>The result detail reason: blocked/unknown blocks name what blocks them.</summary>
    public static string DetailReason(LogicBlockStateDto? state)
    {
        if (state is null)
        {
            return string.Empty;
        }

        if (string.Equals(state.State, LogicBlockStates.Ready, StringComparison.OrdinalIgnoreCase))
        {
            return "all conditions met";
        }

        if (state.State is LogicBlockStates.Blocked or LogicBlockStates.Unknown)
        {
            return string.IsNullOrEmpty(state.Reason) ? string.Empty : $"Blocked by: {state.Reason}";
        }

        return state.Reason ?? string.Empty;
    }

    /// <summary>"2/3 steps · Open valve", "complete", or empty for a non-sequence.</summary>
    public static string SequenceProgress(LogicBlockDto block, LogicBlockStateDto? state)
    {
        if (block.Steps.Count == 0)
        {
            return string.Empty;
        }

        int done = 0;
        string current = string.Empty;
        for (int i = 0; i < block.Steps.Count; i++)
        {
            LogicStepDto step = block.Steps[i];
            string word = state?.Steps.FirstOrDefault(s => s.Id == step.Id)?.State ?? string.Empty;
            if (string.Equals(word, LogicStepStates.Done, StringComparison.OrdinalIgnoreCase))
            {
                done++;
            }
            else if (current.Length == 0
                && (string.Equals(word, LogicStepStates.Current, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(word, LogicStepStates.Unknown, StringComparison.OrdinalIgnoreCase)))
            {
                current = string.IsNullOrWhiteSpace(step.Name) ? $"Step {i + 1}" : step.Name;
            }
        }

        if (done == block.Steps.Count)
        {
            return "complete";
        }

        return $"{done}/{block.Steps.Count} steps" + (current.Length > 0 ? $" · {current}" : string.Empty);
    }

    /// <summary>Seconds with up to two decimals, trailing zeros trimmed ("1.5 s", "3 s").</summary>
    public static string Seconds(int milliseconds)
    {
        double seconds = milliseconds / 1000.0;
        return seconds.ToString("0.##", CultureInfo.InvariantCulture) + " s";
    }
}
