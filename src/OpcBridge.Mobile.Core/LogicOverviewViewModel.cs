using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Client;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// One block on the overview screen: name, kind, the big state word and why it is blocked.
/// The view colours by <see cref="StateKey"/> and still shows the word, so the state reads
/// without colour too.
/// </summary>
public sealed partial class LogicBlockCardViewModel : ObservableObject
{
    private readonly LogicBlockDto block_;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _stateLabel = "no data";
    [ObservableProperty] private string _stateKey = "unknown";
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private string _progressText = string.Empty;

    public LogicBlockCardViewModel(LogicBlockDto block)
    {
        block_ = block;
        ApplyDefinition(block);
    }

    public Guid Id => block_.Id;

    public string KindLabel => block_.Kind switch
    {
        LogicBlockKinds.Sequence => "Sequence",
        LogicBlockKinds.Permissive => "Permissive",
        _ => "Interlock"
    };

    public bool Enabled => block_.Enabled;

    public void ApplyDefinition(LogicBlockDto block)
    {
        Name = block.Name;
        Description = block.Description;
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(Enabled));
    }

    public void ApplyState(LogicBlockStateDto? state)
    {
        if (state is null)
        {
            StateLabel = "no data";
            StateKey = LogicBlockStates.Unknown;
            Reason = "reading…";
            ProgressText = string.Empty;
            return;
        }

        StateKey = state.State;
        StateLabel = state.State switch
        {
            LogicBlockStates.Ready => "READY",
            LogicBlockStates.Blocked => "BLOCKED",
            LogicBlockStates.Disabled => "DISABLED",
            _ => "NO DATA"
        };
        Reason = string.IsNullOrWhiteSpace(state.Reason)
            ? (state.State == LogicBlockStates.Ready ? "all conditions met" : null)
            : state.Reason;

        ProgressText = block_.Kind == LogicBlockKinds.Sequence && block_.Steps.Count > 0
            ? BuildProgress(state)
            : string.Empty;
    }

    private string BuildProgress(LogicBlockStateDto state)
    {
        int total = block_.Steps.Count;
        int done = state.Steps.Count(step => string.Equals(step.State, LogicStepStates.Done, StringComparison.OrdinalIgnoreCase));
        LogicStepStateDto? current = state.Steps.FirstOrDefault(step =>
            string.Equals(step.State, LogicStepStates.Current, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(step.State, LogicStepStates.Unknown, StringComparison.OrdinalIgnoreCase));
        string currentName = current is null
            ? (done == total ? "complete" : string.Empty)
            : block_.Steps.FirstOrDefault(s => s.Id == current.Id)?.Name ?? string.Empty;
        string progress = $"{done}/{total} steps";
        return currentName.Length > 0 ? progress + " · " + currentName : progress;
    }
}

/// <summary>
/// The overview: every block as a card, updated in place from pushed snapshots so the list
/// never rebuilds under the operator's finger.
/// </summary>
public sealed partial class LogicOverviewViewModel : ObservableObject
{
    private readonly Dictionary<Guid, LogicBlockCardViewModel> cards_ = new();

    public ObservableCollection<LogicBlockCardViewModel> Blocks { get; } = new();

    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>The definitions the detail screen is built from (latest applied).</summary>
    public IReadOnlyList<LogicBlockDto> Definitions { get; private set; } = Array.Empty<LogicBlockDto>();

    /// <summary>The latest evaluated state (latest applied).</summary>
    public LogicStateSnapshot? State { get; private set; }

    public void ApplyDefinitions(IEnumerable<LogicBlockDto> blocks)
    {
        List<LogicBlockDto> sorted = blocks
            .OrderBy(block => block.Order)
            .ThenBy(block => block.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Definitions = sorted;

        bool sameOrder = sorted.Count == Blocks.Count;
        if (sameOrder)
        {
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].Id != Blocks[i].Id)
                {
                    sameOrder = false;
                    break;
                }
            }
        }

        if (!sameOrder)
        {
            cards_.Clear();
            Blocks.Clear();
            foreach (LogicBlockDto block in sorted)
            {
                LogicBlockCardViewModel card = new(block);
                cards_[block.Id] = card;
                Blocks.Add(card);
            }
        }
        else
        {
            foreach (LogicBlockDto block in sorted)
            {
                cards_[block.Id].ApplyDefinition(block);
            }
        }

        ApplyState(State);
    }

    public void ApplyState(LogicStateSnapshot? snapshot)
    {
        State = snapshot;
        Dictionary<Guid, LogicBlockStateDto> byId = new();
        foreach (LogicBlockStateDto block in snapshot?.Blocks ?? Enumerable.Empty<LogicBlockStateDto>())
        {
            byId[block.Id] = block;
        }

        foreach (LogicBlockCardViewModel card in Blocks)
        {
            card.ApplyState(byId.TryGetValue(card.Id, out LogicBlockStateDto? state) ? state : null);
        }

        ApplySummary();
    }

    private void ApplySummary()
    {
        if (Blocks.Count == 0)
        {
            Summary = "no logic blocks configured";
            return;
        }

        int ready = Blocks.Count(card => string.Equals(card.StateKey, LogicBlockStates.Ready, StringComparison.OrdinalIgnoreCase));
        int blocked = Blocks.Count(card => string.Equals(card.StateKey, LogicBlockStates.Blocked, StringComparison.OrdinalIgnoreCase));
        int unknown = Blocks.Count(card => string.Equals(card.StateKey, LogicBlockStates.Unknown, StringComparison.OrdinalIgnoreCase));
        Summary = $"{ready} ready · {blocked} blocked" + (unknown > 0 ? $" · {unknown} no data" : string.Empty);
    }
}
