using System.Globalization;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;

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
            LogicElementKinds.Coil => "COIL",
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

    /// <summary>
    /// The result pane's freshness line. The link's own state wins over the age: a dead feed must
    /// never keep claiming a recent update, and a snapshot's age counts up, so a quiet bridge reads
    /// as quiet instead of fresh.
    /// </summary>
    public static string Freshness(BridgeLinkState? link, DateTimeOffset? updatedAt, DateTimeOffset now)
    {
        if (link == BridgeLinkState.Failed)
        {
            return "stale — bridge unreachable";
        }

        if (link == BridgeLinkState.Reconnecting)
        {
            return "stale — reconnecting";
        }

        if (updatedAt is not { } at)
        {
            return "no state yet";
        }

        TimeSpan age = now - at;
        return age.TotalSeconds < 5
            ? "updated just now"
            : age.TotalSeconds < 60
                ? $"updated {(int)age.TotalSeconds}s ago"
                : age.TotalMinutes < 60
                    ? $"updated {(int)age.TotalMinutes}m ago"
                    : $"updated {(int)age.TotalHours}h ago";
    }

    /// <summary>
    /// The block list's empty line. Reading, an unreachable bridge, a read the bridge refused and a
    /// genuinely empty list are four different facts — never one sentence.
    /// </summary>
    public static string BlockListEmptyMessage(bool hasBridge, bool connected, bool loading, string? error) =>
        !hasBridge
            ? "Connect a bridge to load its plant logic."
            : loading
                ? "Reading the bridge's blocks…"
                : !connected
                    ? "The bridge is unreachable — its blocks will appear when it answers."
                    : !string.IsNullOrWhiteSpace(error)
                        ? $"The bridge could not list its blocks — {error}"
                        : "No logic blocks yet — use New block to author the first one.";

    /// <summary>
    /// The result pane's empty state, naming the same fact as the list: no selection, a draft not
    /// saved yet, a read in flight, an unreachable bridge, a refused read or a truly empty list.
    /// </summary>
    public static (string Title, string Message) ResultEmptyState(
        bool hasStoredBlock,
        bool editingNewBlock,
        bool hasBridge,
        bool connected,
        bool loading,
        string? error,
        bool hasBlocks)
    {
        if (hasStoredBlock)
        {
            return ("NOTHING TO EVALUATE", "This block has no elements to evaluate yet.");
        }

        if (editingNewBlock)
        {
            return ("NOT SAVED YET", "Save the block to see its evaluated network.");
        }

        if (!hasBridge)
        {
            return ("NO BRIDGE", "Connect a bridge to load and author its plant logic.");
        }

        if (hasBlocks)
        {
            return ("NO BLOCK SELECTED", "Pick a block in the list to see its evaluated network.");
        }

        if (loading)
        {
            return ("READING…", "Reading the bridge's blocks…");
        }

        if (!connected)
        {
            return ("NO BLOCKS", "The bridge is unreachable — its blocks will appear when it answers.");
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return ("COULD NOT READ", $"The bridge could not list its blocks — {error}");
        }

        return ("NO BLOCKS YET", "Use New block to author the first one.");
    }

    /// <summary>Seconds with up to two decimals, trailing zeros trimmed ("1.5 s", "3 s").</summary>
    public static string Seconds(int milliseconds)
    {
        double seconds = milliseconds / 1000.0;
        return seconds.ToString("0.##", CultureInfo.InvariantCulture) + " s";
    }

    // ---- Area tags (the area's named tags) ----

    /// <summary>An entry that has been created but has no bridge tag yet.</summary>
    public const string TagNotAssigned = "(not assigned)";

    /// <summary>What the Tags tab's area field means, and what saving it will do.</summary>
    public static string AreaTagsHint(bool blank, bool knownArea, string name) =>
        blank
            ? "Name the area (e.g. DRY END) — every block of that area shares these tags."
            : knownArea
                ? $"Every {name} block shares these tags — assign a tag here and every one of their rows reads it."
                : $"New area tags — saving creates the list every {name} block's rows read.";

    /// <summary>The message when an area is typed and the block's direct tags join it (stored by Save).</summary>
    public static string AreaTagsMoved(int count, string name) =>
        $"Moved {(count == 1 ? "1 tag" : $"{count} tags")} into \"{name}\" — Save to store them.";

    /// <summary>The save's note: the direct tags it carried joined the area and are shared by it.</summary>
    public static string AreaTagsShared(int count, string name) =>
        $"{(count == 1 ? "1 tag" : $"{count} tags")} now shared by \"{name}\".";

    /// <summary>The save refusal when the block has no area yet.</summary>
    public const string AreaNameRequired =
        "Name the area (e.g. DRY END) first — the area's tags are shared by every block of the area.";

    /// <summary>The tab's empty state: nothing referenced, nothing created.</summary>
    public const string AreaTagsEmpty =
        "No tags yet — add one with New tag, name it and assign its source and tag; every block's rows pick it from their TAG list.";

    /// <summary>The tab's hint on a <c>+ New tag</c> row, before a source is chosen (its tags list here).</summary>
    public const string TagSourceFirstHint = "Choose a source — its mapped tags list here.";

    /// <summary>The tab's hint when the chosen source has no mapped tags to bind.</summary>
    public static string NoMappedTagsHint(string sourceName) =>
        $"No mapped tags on {sourceName} — map them in the dashboard (Tags ▸ Maps), then reopen this page.";

    /// <summary>
    /// An editor row's note for a tag the block still carries directly: a block authored before
    /// the area's tags existed keeps reading it until the list holds it.
    /// </summary>
    public static string StoredTagHint(string label) =>
        $"Direct tag: {label} — add it to this area's tags (the Tags tab) to assign it there.";

    /// <summary>The editor's pointer at the Tags tab while the area holds no tags yet.</summary>
    public const string AreaTagsPrompt =
        "The block's rows pick their tag from the area's tags (the Tags tab) — none yet: add each signal here with its source and tag.";

    /// <summary>The Tags tab's note for a bound tag that is not mapped for writing.</summary>
    public const string ReadOnlyTagHint = "read-only tag — an action cannot write it.";

    /// <summary>
    /// The scope line while the block has no area of its own: its tags resolve through the legacy
    /// fallback (the name it stores, else its interlock group), and typing an area moves them.
    /// </summary>
    public static string AreaTagsFallbackHint(string name) => name.Length == 0
        ? "No area set — type one (e.g. DRY END) so this block's named tags resolve."
        : $"No area set — this block reads the \"{name}\" tags (its stored name or interlock group). Type an area to scope the tags by area.";

    /// <summary>
    /// The Tags tab's live chip: the tag's current value (1 / 0 for a bit, the number otherwise),
    /// "bad" for bad quality and "—" when the tag is unassigned or not on the bridge.
    /// </summary>
    public static string TagLiveChip(MultiBridgeTagEntry? entry)
    {
        if (entry is null)
        {
            return "—";
        }

        if (entry.IsGood == false)
        {
            return "bad";
        }

        object? value = entry.Value;
        if (value is null)
        {
            return "—";
        }

        string text = value switch
        {
            bool flag => flag ? "1" : "0",
            double number => number.ToString("0.###", CultureInfo.InvariantCulture),
            float number => number.ToString("0.###", CultureInfo.InvariantCulture),
            decimal number => number.ToString("0.###", CultureInfo.InvariantCulture),
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—",
            System.Text.Json.JsonElement element => element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => "1",
                System.Text.Json.JsonValueKind.False => "0",
                System.Text.Json.JsonValueKind.String => element.GetString() ?? "—",
                System.Text.Json.JsonValueKind.Null => "—",
                _ => element.ToString()
            },
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—"
        };

        return string.IsNullOrWhiteSpace(entry.Unit) ? text : text + " " + entry.Unit;
    }
}
