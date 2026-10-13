using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// One row of the Result tab's network view: the definition's sentence, kind and preset joined
/// with the evaluated state — mark, live value, "should X · actual Y", timer/counter progress and
/// the next-step hint while the element is not satisfied.
/// </summary>
public sealed partial class LogicResultElementRowViewModel : ObservableObject
{
    public LogicResultElementRowViewModel(
        LogicElementDto element,
        int depth,
        LogicTagCatalog catalog,
        LogicAreaTagsDto? tagSource = null)
    {
        ElementId = element.Id;
        Kind = element.Kind;
        Op = element.Op;
        Depth = Math.Max(0, depth);
        Guides = new int[Depth];
        Text = element.Text;
        WarnOnly = string.Equals(element.Severity, LogicConditionSeverities.Warn, StringComparison.OrdinalIgnoreCase);
        KindLabel = LogicText.ElementKindLabel(Kind, Op);
        PresetPv = element.Pv;
        IsContact = string.Equals(Kind, LogicElementKinds.Contact, StringComparison.OrdinalIgnoreCase);

        // A relay element names its relay where a tag contact names its tag — a coil reads as
        // "COIL · relay M01 · 1", a relay contact as "NO · relay M01 · should 1 · actual 0"; a
        // contact reading the area's named tags shows the entry and the tag it is bound to.
        bool relayElement = !string.IsNullOrWhiteSpace(element.Relay);
        TagLabel = relayElement
            ? "relay " + element.Relay
            : IsContact
                ? TagContactLabel(element, catalog, tagSource)
                : string.Empty;
        nextStepCandidate_ = element.NextStepText;
    }

    private static string TagContactLabel(LogicElementDto element, LogicTagCatalog catalog, LogicAreaTagsDto? tagSource)
    {
        if (!string.IsNullOrWhiteSpace(element.Tag))
        {
            string name = element.Tag.Trim();
            return LogicAreaTags.TryResolve(tagSource, name, out string sourceId, out string itemId)
                ? name + " · " + catalog.TagLabel(sourceId, itemId)
                : LogicAreaTags.UnresolvedText(name, tagSource, tagSource?.Name);
        }

        return element.ItemId.Length > 0 ? catalog.TagLabel(element.SourceId, element.ItemId) : string.Empty;
    }

    private readonly string? nextStepCandidate_;

    public Guid ElementId { get; }

    public string Kind { get; }

    public string Op { get; }

    public int Depth { get; }

    public string Text { get; }

    public bool WarnOnly { get; }

    public string KindLabel { get; }

    public int PresetPv { get; }

    public bool IsContact { get; }

    public string TagLabel { get; }

    public bool HasTag => TagLabel.Length > 0;

    /// <summary>
    /// One entry per level enclosing this row: the row draws a rail for each, so the nesting the
    /// indentation implies is drawn, not guessed. A rail sits where its parent row's content starts.
    /// </summary>
    public IReadOnlyList<int> Guides { get; }

    [ObservableProperty]
    private string _mark = "—";

    [ObservableProperty]
    private string _actual = "—";

    [ObservableProperty]
    private string _valueText = "—";

    [ObservableProperty]
    private string _shouldLine = string.Empty;

    [ObservableProperty]
    private string _progress = string.Empty;

    [ObservableProperty]
    private string _nextStepText = string.Empty;

    [ObservableProperty]
    private bool _isOk;

    [ObservableProperty]
    private bool _isBad;

    [ObservableProperty]
    private bool _isWarn = true;

    public bool HasShould => ShouldLine.Length > 0;

    public bool IsShouldOk { get; private set; }

    public bool IsShouldBad { get; private set; }

    public bool HasProgress => Progress.Length > 0;

    public bool HasNextStep => NextStepText.Length > 0;

    partial void OnShouldLineChanged(string value)
    {
        OnPropertyChanged(nameof(HasShould));
        OnPropertyChanged(nameof(IsShouldOk));
        OnPropertyChanged(nameof(IsShouldBad));
    }

    partial void OnProgressChanged(string value) => OnPropertyChanged(nameof(HasProgress));

    partial void OnNextStepTextChanged(string value) => OnPropertyChanged(nameof(HasNextStep));

    public void Apply(LogicElementStateDto? state)
    {
        Mark = LogicText.Mark(state?.State);
        Actual = LogicText.Actual(state, Kind, Op);
        ValueText = string.IsNullOrEmpty(state?.ValueText) ? "—" : state!.ValueText;
        IsShouldOk = state?.Matches == true;
        IsShouldBad = state?.Matches == false;
        ShouldLine = LogicText.ShouldLine(state, Kind, Op);
        Progress = LogicText.Progress(state, PresetPv);
        NextStepText = state is null
            || !string.Equals(state.State, LogicConditionStates.True, StringComparison.OrdinalIgnoreCase)
                ? nextStepCandidate_ ?? string.Empty
                : string.Empty;

        string key = LogicText.ConditionStateKey(state?.State);
        IsOk = key == "ok";
        IsBad = key == "bad";
        IsWarn = key == "warn";
    }
}

/// <summary>One step card of a sequence block in the Result tab, with its own element rows.</summary>
public sealed partial class LogicResultStepRowViewModel : ObservableObject
{
    public LogicResultStepRowViewModel(LogicStepDto step, int number)
    {
        StepId = step.Id;
        Name = string.IsNullOrWhiteSpace(step.Name) ? $"Step {number}" : step.Name;
    }

    public Guid StepId { get; }

    public string Name { get; }

    public ObservableCollection<LogicResultElementRowViewModel> Elements { get; } = new();

    [ObservableProperty]
    private string _stateWord = "No data";

    [ObservableProperty]
    private string _reason = string.Empty;

    [ObservableProperty]
    private bool _isDone;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isPending;

    [ObservableProperty]
    private bool _isNoData = true;

    public bool HasReason => Reason.Length > 0;

    partial void OnReasonChanged(string value) => OnPropertyChanged(nameof(HasReason));

    public void Apply(LogicStepStateDto? state)
    {
        StateWord = LogicText.StepStateWord(state?.State);
        IsDone = string.Equals(state?.State, LogicStepStates.Done, StringComparison.OrdinalIgnoreCase);
        IsCurrent = string.Equals(state?.State, LogicStepStates.Current, StringComparison.OrdinalIgnoreCase);
        IsPending = string.Equals(state?.State, LogicStepStates.Pending, StringComparison.OrdinalIgnoreCase);
        IsNoData = !IsDone && !IsCurrent && !IsPending;
        Reason = state?.Reason ?? string.Empty;
    }
}
