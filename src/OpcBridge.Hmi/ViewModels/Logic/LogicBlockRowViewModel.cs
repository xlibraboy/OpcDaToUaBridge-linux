using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>One bridge the Logic page can show: its id and live link state.</summary>
public sealed record LogicBridgeOption(string BridgeId, BridgeLinkState State)
{
    public string Label => BridgeId;

    public bool IsConnected => State == BridgeLinkState.Connected;

    public string StatusText => State switch
    {
        BridgeLinkState.Connected => "Connected",
        BridgeLinkState.Reconnecting => "Reconnecting",
        _ => "Failed"
    };

    public string StatusKey => State switch
    {
        BridgeLinkState.Connected => "ok",
        BridgeLinkState.Reconnecting => "warn",
        _ => "bad"
    };
}

/// <summary>A block as a row of the list / blocks window, with its live evaluated state.</summary>
public sealed partial class LogicBlockRowViewModel : ObservableObject
{
    public LogicBlockRowViewModel(LogicBlockDto block)
    {
        Block = block;
    }

    public LogicBlockDto Block { get; }

    public Guid BlockId => Block.Id;

    public string Name => string.IsNullOrWhiteSpace(Block.Name) ? "(unnamed)" : Block.Name;

    public string Group => Block.Group?.Trim() ?? string.Empty;

    public string KindLabel => LogicText.BlockKindLabel(Block.Kind);

    [ObservableProperty]
    private string _stateWord = "NO DATA";

    [ObservableProperty]
    private bool _isOk;

    [ObservableProperty]
    private bool _isBad;

    [ObservableProperty]
    private bool _isWarn = true;

    [ObservableProperty]
    private bool _isMuted;

    /// <summary>Set by the panel: the row the editor is on (drives the accent bar).</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _reasonLine = "reading…";

    [ObservableProperty]
    private string _progress = string.Empty;

    public bool HasProgress => Progress.Length > 0;

    partial void OnProgressChanged(string value) => OnPropertyChanged(nameof(HasProgress));

    public void ApplyState(LogicBlockStateDto? state)
    {
        StateWord = LogicText.StateWord(state?.State);
        string key = LogicText.StateKey(state?.State);
        IsOk = key == "ok";
        IsBad = key == "bad";
        IsMuted = key == "muted";
        IsWarn = key == "warn";
        ReasonLine = LogicText.ReasonLine(state);
        Progress = LogicText.SequenceProgress(Block, state);
    }
}

/// <summary>
/// One heading of the grouped block list: the interlock group's blocks and the "N blocks · M
/// blocked" summary that stays visible. A single ungrouped list hides its heading.
/// </summary>
public sealed partial class LogicGroupViewModel : ObservableObject
{
    public LogicGroupViewModel(string name, bool showHeading, IEnumerable<LogicBlockRowViewModel> blocks)
    {
        Name = name;
        ShowHeading = showHeading;
        Blocks = new ObservableCollection<LogicBlockRowViewModel>(blocks);
        UpdateSummary();
    }

    public string Name { get; }

    public bool ShowHeading { get; }

    public ObservableCollection<LogicBlockRowViewModel> Blocks { get; }

    [ObservableProperty]
    private string _summary = string.Empty;

    public void UpdateSummary()
    {
        int blocked = Blocks.Count(row => row.IsBad);
        int noData = Blocks.Count(row => row.IsWarn);
        string text = Blocks.Count == 1 ? "1 block" : $"{Blocks.Count} blocks";
        if (blocked > 0)
        {
            text += $" · {blocked} blocked";
        }

        if (noData > 0)
        {
            text += $" · {noData} no data";
        }

        Summary = text;
    }
}
