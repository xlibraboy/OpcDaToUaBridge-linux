using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Designer.Services;
using OpcBridge.Hmi.Services;
using OpcBridge.Hmi.ViewModels;
using OpcBridge.Hmi.ViewModels.Widgets;

namespace OpcBridge.Hmi.Designer.ViewModels;

public partial class DesignerViewModel : ObservableObject, IDisposable
{
    private const double SnapGrid = 8;
    private const double NudgeFine = 1;
    private const double NudgeCoarse = SnapGrid;
    private const int UndoLimit = 50;

    /// <summary>The bridge id the runtime's primary bridge uses, so designer bindings resolve there.</summary>
    public const string PrimaryBridgeId = "default";

    private readonly DisplayStoreClient store_ = new();
    private readonly MultiBridgeTagCache cache_ = new();
    private readonly IDesignerBridgeClient bridge_;
    private readonly IDesignerLiveLink live_;
    private readonly CancellationTokenSource lifetime_ = new();
    private readonly List<string> undoStack_ = new();
    private readonly List<string> redoStack_ = new();
    private DisplayWidgetDto? clipboard_;
    private DisplayDocumentDto document_ = NewDocument();
    private bool connecting_;
    private bool disposed_;
    private bool sourcesLoadedFromBridge_;

    public DesignerViewModel()
        : this(System.Environment.GetCommandLineArgs().Skip(1).ToArray())
    {
    }

    public DesignerViewModel(string[] args)
        : this(args, null, null)
    {
    }

    public DesignerViewModel(string[] args, IDesignerBridgeClient? bridge, IDesignerLiveLink? live)
    {
        bridge_ = bridge ?? new DesignerBridgeClient();
        live_ = live ?? new DesignerLiveLink(cache_);
        live_.StateChanged += OnLiveStateChanged;

        StoreUrl = ResolveInitialStoreUrl(args);
        Surface = new DisplaySurfaceViewModel(cache_, _ => { }, (_, _) => Task.FromResult((true, (string?)null)));
        Surface.ApplyDesignMode(true);
        Surface.ShowGrid = true;
        Surface.SnapStep = SnapEnabled ? SnapGrid : null;
        Surface.EditStarted += PushUndo;
        Surface.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DisplaySurfaceViewModel.SelectedWidget))
            {
                SelectedWidget = Surface.SelectedWidget;
            }
        };
        ReloadSurface();
        _ = DetectStoreAsync();
    }

    private async Task DetectStoreAsync()
    {
        string? found = await bridge_.DetectAsync(lifetime_.Token).ConfigureAwait(true);
        if (found is not null)
        {
            string current = StoreUrl.Trim().TrimEnd('/');
            if (current is "" or "http://127.0.0.1:8080" or "http://localhost:8080")
            {
                StoreUrl = found;
                StatusMessage = $"Local OpcBridge detected at {found}";
            }
            else
            {
                StatusMessage = $"Using configured store {StoreUrl}";
            }
        }
        else
        {
            StatusMessage = "Local OpcBridge not detected — using " + StoreUrl;
        }

        await ConnectBridgeAsync().ConfigureAwait(true);
        await RefreshListAsync().ConfigureAwait(true);
    }

    public DisplaySurfaceViewModel Surface { get; }

    /// <summary>Pass-through of the surface selection for the property panel.</summary>
    [ObservableProperty]
    private WidgetViewModelBase? _selectedWidget;

    public ObservableCollection<string> Palette { get; } = new(
    [
        DisplayWidgetTypes.Label,
        DisplayWidgetTypes.Numeric,
        DisplayWidgetTypes.QualityLamp,
        DisplayWidgetTypes.BoolIndicator,
        DisplayWidgetTypes.PushButton
    ]);

    public ObservableCollection<DisplayListItemDto> ExistingDisplays { get; } = new();

    [ObservableProperty]
    private string _storeUrl = "http://127.0.0.1:8080";

    /// <summary>Initial store URL: --store takes precedence, then HMI_STORE_URL, then default.</summary>
    public static string ResolveInitialStoreUrl(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].Trim();
            if (arg.StartsWith("--store=", StringComparison.OrdinalIgnoreCase))
            {
                string value = arg["--store=".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.TrimEnd('/');
                }
            }
            else if (string.Equals(arg, "--store", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                string value = args[i + 1].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.TrimEnd('/');
                }
            }
        }

        string env = (Environment.GetEnvironmentVariable("HMI_STORE_URL") ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(env) ? "http://127.0.0.1:8080" : env.TrimEnd('/');
    }

    [ObservableProperty]
    private string _documentId = "plant-overview";

    [ObservableProperty]
    private string _documentName = "Plant Overview";

    [ObservableProperty]
    private int _documentVersion;

    [ObservableProperty]
    private string _selectedPaletteType = DisplayWidgetTypes.Numeric;

    [ObservableProperty]
    private string _statusMessage = "Designer ready — connect store to Open/Save.";

    [ObservableProperty]
    private DisplayListItemDto? _selectedExisting;

    [ObservableProperty]
    private bool _snapEnabled = true;

    [ObservableProperty]
    private bool _showGrid = true;

    partial void OnSnapEnabledChanged(bool value) => Surface.SnapStep = value ? SnapGrid : null;

    partial void OnShowGridChanged(bool value) => Surface.ShowGrid = value;

    partial void OnSelectedWidgetChanged(WidgetViewModelBase? value)
    {
        RefreshBindingPanel();
        NotifySelectionCommands();
    }

    // ---- Bridge link (sources, sign-in, live values) ----

    /// <summary>Sources on the connected bridge, with type badge and connection state.</summary>
    public ObservableCollection<BridgeSourceInfo> BridgeSources { get; } = new();

    /// <summary>The source the tag picker opens on; picking one here also scopes "Choose tag…".</summary>
    [ObservableProperty]
    private BridgeSourceInfo? _selectedSource;

    /// <summary>Bridge id written into bindings; the runtime's primary bridge is "default".</summary>
    [ObservableProperty]
    private string _defaultBridgeId = PrimaryBridgeId;

    [ObservableProperty]
    private bool _authEnabled;

    [ObservableProperty]
    private bool _isSignedIn;

    [ObservableProperty]
    private string? _signedInAs;

    /// <summary>Why the source list could not be loaded (unreachable, auth wall, …).</summary>
    [ObservableProperty]
    private string? _sourcesError;

    public bool HasBridgeSources => BridgeSources.Count > 0;

    public bool HasSourcesError => !string.IsNullOrWhiteSpace(SourcesError);

    /// <summary>The list is shown whenever there is something to pick from.</summary>
    public bool ShowSourcesList => HasBridgeSources;

    /// <summary>The teach-the-space empty state — only when there is no error to explain first.</summary>
    public bool ShowSourcesEmpty => !HasBridgeSources && !HasSourcesError;

    /// <summary>Sign-in is only offered while the bridge reports that auth is enabled.</summary>
    public bool ShowSignIn => AuthEnabled && !IsSignedIn;

    public bool ShowSignedIn => IsSignedIn;

    public DesignerLiveState LiveState => live_.State;

    public bool IsLive => LiveState == DesignerLiveState.Live;

    public bool IsSnapshot => LiveState == DesignerLiveState.Snapshot;

    public bool IsOffline => LiveState == DesignerLiveState.Offline;

    public string LiveStateTooltip => LiveState switch
    {
        DesignerLiveState.Live => "Live values are streaming from the bridge.",
        DesignerLiveState.Snapshot => live_.LastError ?? "Showing the last tag snapshot; live values are not streaming.",
        _ => live_.LastError ?? "The bridge is not reachable."
    };

    /// <summary>Raised after live values or the tag snapshot changed (background thread).</summary>
    public event Action? LiveValuesChanged;

    public async Task ConnectBridgeAsync()
    {
        if (disposed_ || connecting_)
        {
            return;
        }

        string url = StoreUrl.Trim().TrimEnd('/');
        if (url.Length == 0)
        {
            StatusMessage = "Enter the bridge address, then Refresh.";
            return;
        }

        connecting_ = true;
        try
        {
            bridge_.SetBaseAddress(url);
            store_.SetBaseAddress(url);

            BridgeAuthInfo? auth = await bridge_.GetAuthInfoAsync(lifetime_.Token).ConfigureAwait(true);
            if (auth is null)
            {
                AuthEnabled = false;
                IsSignedIn = false;
                SignedInAs = null;
                SourcesError = "Bridge not reachable at " + url;
                ClearSources();
            }
            else
            {
                AuthEnabled = auth.AuthEnabled;
                IsSignedIn = auth.Authenticated;
                SignedInAs = auth.Authenticated ? DescribeUser(auth) : null;
                await RefreshSourcesCoreAsync().ConfigureAwait(true);
            }

            await live_.StartAsync(url, PrimaryBridgeId, OnLiveChanged, lifetime_.Token).ConfigureAwait(true);
            if (LiveState == DesignerLiveState.Offline && SourcesError is null)
            {
                SourcesError = live_.LastError ?? "Bridge not reachable.";
                StatusMessage = SourcesError;
            }
        }
        catch (Exception ex)
        {
            SourcesError = "Bridge connect failed: " + ex.Message;
            StatusMessage = SourcesError;
        }
        finally
        {
            connecting_ = false;
        }
    }

    [RelayCommand]
    private async Task RefreshBridgeAsync()
    {
        await ConnectBridgeAsync().ConfigureAwait(true);
        await RefreshListAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshSourcesAsync()
    {
        if (disposed_)
        {
            return;
        }

        try
        {
            bridge_.SetBaseAddress(StoreUrl.Trim().TrimEnd('/'));
            await RefreshSourcesCoreAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SourcesError = "Source list failed: " + ex.Message;
            StatusMessage = SourcesError;
        }
    }

    private async Task RefreshSourcesCoreAsync()
    {
        SourceListResult result = await bridge_.GetSourcesAsync(lifetime_.Token).ConfigureAwait(true);
        if (result.Ok)
        {
            sourcesLoadedFromBridge_ = true;
            SourcesError = null;
            RebuildSources(result.Sources);
            return;
        }

        // Signed out (or the list is unavailable): derive what we can from the tag snapshot.
        sourcesLoadedFromBridge_ = false;
        SourcesError = result.Error;
        RebuildSources(DesignerSourceCatalog.Build(cache_.Tags, null));
    }

    private void RebuildSources(IReadOnlyList<BridgeSourceInfo> sources)
    {
        string? selectedId = SelectedSource?.SourceId;
        BridgeSources.Clear();
        foreach (BridgeSourceInfo source in sources)
        {
            BridgeSources.Add(source);
        }

        SelectedSource = BridgeSources.FirstOrDefault(
            source => string.Equals(source.SourceId, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? BridgeSources.FirstOrDefault();
        NotifySourcesChanged();
    }

    private void ClearSources()
    {
        BridgeSources.Clear();
        SelectedSource = null;
        NotifySourcesChanged();
    }

    private void NotifySourcesChanged()
    {
        OnPropertyChanged(nameof(HasBridgeSources));
        OnPropertyChanged(nameof(ShowSourcesList));
        OnPropertyChanged(nameof(ShowSourcesEmpty));
    }

    public async Task<(bool Ok, string? Error)> SignInAsync(string username, string password)
    {
        try
        {
            (bool ok, string? error) = await bridge_.SignInAsync(username, password, lifetime_.Token)
                .ConfigureAwait(true);
            if (!ok)
            {
                return (false, error);
            }
        }
        catch (Exception ex)
        {
            return (false, "Sign-in failed: " + ex.Message);
        }

        BridgeAuthInfo? auth = await bridge_.GetAuthInfoAsync(lifetime_.Token).ConfigureAwait(true);
        IsSignedIn = auth?.Authenticated ?? true;
        SignedInAs = auth is not null ? DescribeUser(auth) : username;
        StatusMessage = $"Signed in as {SignedInAs}";
        await RefreshSourcesCoreAsync().ConfigureAwait(true);
        return (true, null);
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await bridge_.SignOutAsync(lifetime_.Token).ConfigureAwait(true);
        IsSignedIn = false;
        SignedInAs = null;
        StatusMessage = "Signed out";
        await RefreshSourcesCoreAsync().ConfigureAwait(true);
    }

    private static string DescribeUser(BridgeAuthInfo auth)
    {
        string name = string.IsNullOrWhiteSpace(auth.DisplayName) ? auth.Username ?? "(unknown)" : auth.DisplayName!;
        return string.IsNullOrWhiteSpace(auth.Role) ? name : $"{name} ({auth.Role})";
    }

    partial void OnAuthEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSignIn));
        OnPropertyChanged(nameof(ShowSignedIn));
    }

    partial void OnIsSignedInChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSignIn));
        OnPropertyChanged(nameof(ShowSignedIn));
    }

    partial void OnSourcesErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSourcesError));
        OnPropertyChanged(nameof(ShowSourcesEmpty));
    }

    private void OnLiveStateChanged()
    {
        _ = PostToUiAsync(() =>
        {
            OnPropertyChanged(nameof(LiveState));
            OnPropertyChanged(nameof(IsLive));
            OnPropertyChanged(nameof(IsSnapshot));
            OnPropertyChanged(nameof(IsOffline));
            OnPropertyChanged(nameof(LiveStateTooltip));
            OnPropertyChanged(nameof(SelectedBindingMissing));
        });
    }

    /// <summary>Snapshot or value delta arrived on a background thread: surface it on the UI thread.</summary>
    private void OnLiveChanged()
    {
        _ = PostToUiAsync(() =>
        {
            Surface.RefreshLiveValues();
            RefreshBindingPanel();
            RefreshSourcesFromSnapshotIfNeeded();
            LiveValuesChanged?.Invoke();
        });
    }

    /// <summary>Signed out, the source list lives off the tag snapshot — keep it in step.</summary>
    private void RefreshSourcesFromSnapshotIfNeeded()
    {
        if (sourcesLoadedFromBridge_)
        {
            return;
        }

        IReadOnlyList<BridgeSourceInfo> derived = DesignerSourceCatalog.Build(cache_.Tags, null);
        // Values arrive many times a second; only touch the collection when the source set changed,
        // or the list would rebuild (and drop hover/selection) on every delta.
        if (SameSourceIds(derived, BridgeSources))
        {
            return;
        }

        RebuildSources(derived);
    }

    private static bool SameSourceIds(
        IReadOnlyList<BridgeSourceInfo> left,
        ObservableCollection<BridgeSourceInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].SourceId, right[i].SourceId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static Task PostToUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    // ---- Tag binding for the selected widget ----

    public bool ShowBindingPanel =>
        SelectedWidget is not null
        && !string.Equals(SelectedWidget.Type, DisplayWidgetTypes.Label, StringComparison.OrdinalIgnoreCase);

    public bool HasBoundTag => SelectedWidget?.Binding is not null;

    public string SelectedBindingSourceName
    {
        get
        {
            TagBindingKey? binding = SelectedWidget?.Binding;
            if (binding is null)
            {
                return string.Empty;
            }

            BridgeSourceInfo? source = BridgeSources.FirstOrDefault(
                candidate => string.Equals(candidate.SourceId, binding.Value.SourceId, StringComparison.OrdinalIgnoreCase));
            if (source is not null)
            {
                return source.DisplayNameOrId;
            }

            return cache_.TryGet(binding.Value, out MultiBridgeTagEntry? entry) && entry is not null
                ? entry.SourceName
                : binding.Value.SourceId;
        }
    }

    public string SelectedBindingTagName
    {
        get
        {
            TagBindingKey? binding = SelectedWidget?.Binding;
            if (binding is null)
            {
                return string.Empty;
            }

            return cache_.TryGet(binding.Value, out MultiBridgeTagEntry? entry) && entry is not null
                ? entry.DisplayName
                : binding.Value.DaItemId;
        }
    }

    public string SelectedBindingItemId => SelectedWidget?.Binding?.DaItemId ?? string.Empty;

    public string SelectedBindingValueText => SelectedWidget?.ValueText ?? "—";

    public string SelectedBindingQualityText => SelectedWidget?.QualityText ?? string.Empty;

    public bool? SelectedBindingIsGood => SelectedWidget?.IsGood;

    /// <summary>Bound tag that is not in the bridge's mapped-tag snapshot (unmapped, or source down).</summary>
    public bool SelectedBindingMissing
    {
        get
        {
            TagBindingKey? binding = SelectedWidget?.Binding;
            if (binding is null || LiveState == DesignerLiveState.Offline)
            {
                return false;
            }

            return !(cache_.TryGet(binding.Value, out MultiBridgeTagEntry? entry) && entry is not null);
        }
    }

    public string SelectedBindingBridgeId => SelectedWidget?.Binding?.BridgeId ?? DefaultBridgeId;

    /// <summary>Builds a picker scoped to the selected widget's source, else the rail's source.</summary>
    public DesignerTagPickerViewModel CreateTagPicker()
    {
        string? sourceId = SelectedWidget?.Binding?.SourceId ?? SelectedSource?.SourceId;
        return new DesignerTagPickerViewModel(BridgeSources.ToList(), cache_, sourceId);
    }

    /// <summary>Binds the confirmed picker tag to the selected widget.</summary>
    public void BindSelectedTag(TagBindingKey key)
    {
        if (SelectedWidget is not { } widget)
        {
            StatusMessage = "Select a widget on the canvas, then choose a tag.";
            return;
        }

        if (string.Equals(widget.Type, DisplayWidgetTypes.Label, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Label widgets render text only; select a data widget to bind a tag.";
            return;
        }

        PushUndo();
        TagBindingKey binding = TagBindingKey.Create(
            string.IsNullOrWhiteSpace(DefaultBridgeId) ? PrimaryBridgeId : DefaultBridgeId.Trim(),
            key.SourceId,
            key.DaItemId);
        widget.UpdateBinding(binding);
        RefreshBindingPanel();
        StatusMessage = $"{widget.Id} bound to {binding.SourceId} · {binding.DaItemId}";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void UnbindSelected()
    {
        if (SelectedWidget is not { } widget || widget.Binding is null)
        {
            return;
        }

        PushUndo();
        widget.UpdateBinding(null);
        RefreshBindingPanel();
        StatusMessage = $"{widget.Id} unbound";
    }

    private void RefreshBindingPanel()
    {
        OnPropertyChanged(nameof(ShowBindingPanel));
        OnPropertyChanged(nameof(HasBoundTag));
        OnPropertyChanged(nameof(SelectedBindingSourceName));
        OnPropertyChanged(nameof(SelectedBindingTagName));
        OnPropertyChanged(nameof(SelectedBindingItemId));
        OnPropertyChanged(nameof(SelectedBindingValueText));
        OnPropertyChanged(nameof(SelectedBindingQualityText));
        OnPropertyChanged(nameof(SelectedBindingIsGood));
        OnPropertyChanged(nameof(SelectedBindingMissing));
        OnPropertyChanged(nameof(SelectedBindingBridgeId));
        UnbindSelectedCommand.NotifyCanExecuteChanged();
    }

    // ---- Undo / redo ----

    public bool CanUndo => undoStack_.Count > 0;

    public bool CanRedo => redoStack_.Count > 0;

    private void NotifyUndoRedo()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void NotifySelectionCommands()
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        DuplicateSelectedCommand.NotifyCanExecuteChanged();
        AlignLeftCommand.NotifyCanExecuteChanged();
        AlignCenterXCommand.NotifyCanExecuteChanged();
        AlignRightCommand.NotifyCanExecuteChanged();
        AlignTopCommand.NotifyCanExecuteChanged();
        AlignCenterYCommand.NotifyCanExecuteChanged();
        AlignBottomCommand.NotifyCanExecuteChanged();
        RaiseZCommand.NotifyCanExecuteChanged();
        LowerZCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(IsNumericWidget));
        OnPropertyChanged(nameof(SelectedUnitSource));
        OnPropertyChanged(nameof(ShowManualUnit));
        OnPropertyChanged(nameof(SelectedUnit));
        OnPropertyChanged(nameof(SelectedIsTextLabel));
    }

    /// <summary>Snapshot the current document (surface geometry synced) for undo.</summary>
    private void PushUndo()
    {
        SyncDocumentFromSurface();
        undoStack_.Add(JsonSerializer.Serialize(document_));
        if (undoStack_.Count > UndoLimit)
        {
            undoStack_.RemoveAt(0);
        }

        redoStack_.Clear();
        NotifyUndoRedo();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (undoStack_.Count == 0)
        {
            return;
        }

        SyncDocumentFromSurface();
        redoStack_.Add(JsonSerializer.Serialize(document_));
        string json = undoStack_[^1];
        undoStack_.RemoveAt(undoStack_.Count - 1);
        document_ = DeserializeDocument(json);
        RestoreFromDocument();
        NotifyUndoRedo();
        StatusMessage = "Undo";
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (redoStack_.Count == 0)
        {
            return;
        }

        SyncDocumentFromSurface();
        undoStack_.Add(JsonSerializer.Serialize(document_));
        string json = redoStack_[^1];
        redoStack_.RemoveAt(redoStack_.Count - 1);
        document_ = DeserializeDocument(json);
        RestoreFromDocument();
        NotifyUndoRedo();
        StatusMessage = "Redo";
    }

    private static DisplayDocumentDto DeserializeDocument(string json)
        => JsonSerializer.Deserialize<DisplayDocumentDto>(json) ?? NewDocument();

    private void RestoreFromDocument()
    {
        DocumentId = document_.Id;
        DocumentName = document_.Name;
        ReloadSurface();
    }

    // ---- Widget operations ----

    public bool HasSelection => SelectedWidget is not null;

    [RelayCommand]
    private void NewDisplay()
    {
        PushUndo();
        document_ = NewDocument();
        DocumentId = document_.Id;
        DocumentName = document_.Name;
        DocumentVersion = 0;
        ReloadSurface();
        StatusMessage = "New display";
    }

    [RelayCommand]
    private void AddWidget()
    {
        PushUndo();
        string type = string.IsNullOrWhiteSpace(SelectedPaletteType)
            ? DisplayWidgetTypes.Label
            : SelectedPaletteType;
        var widget = new DisplayWidgetDto
        {
            Id = "w" + Guid.NewGuid().ToString("N")[..8],
            Type = type,
            X = 40 + document_.Widgets.Count * 12,
            Y = 40 + document_.Widgets.Count * 12,
            W = type == DisplayWidgetTypes.Label ? 180 : 140,
            H = type == DisplayWidgetTypes.Label ? 28 : 48,
            Props = new Dictionary<string, JsonElement>()
        };

        if (type == DisplayWidgetTypes.Label)
        {
            widget.Props["text"] = JsonSerializer.SerializeToElement(DocumentName);
        }
        else
        {
            widget.Props["label"] = JsonSerializer.SerializeToElement(type);
            if (type == DisplayWidgetTypes.PushButton)
            {
                widget.Props["text"] = JsonSerializer.SerializeToElement("Write");
                widget.Props["writeValue"] = JsonSerializer.SerializeToElement(true);
            }
        }

        document_.Widgets.Add(widget);
        document_.Name = DocumentName;
        document_.Id = DocumentId;
        ReloadSurface();
        SelectWidgetById(widget.Id);
        StatusMessage = $"Added {type} ({widget.Id}) — choose a tag to bind";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected()
    {
        if (SelectedWidget is null)
        {
            return;
        }

        PushUndo();
        string id = SelectedWidget.Id;
        document_.Widgets = document_.Widgets.Where(w => w.Id != id).ToList();
        ReloadSurface();
        StatusMessage = $"Deleted widget {id}";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopySelected()
    {
        if (SelectedWidget is null)
        {
            return;
        }

        clipboard_ = CloneWidget(FindDto(SelectedWidget.Id));
        StatusMessage = $"Copied {clipboard_.Type} {clipboard_.Id}";
    }

    [RelayCommand]
    private void Paste()
    {
        if (clipboard_ is null)
        {
            StatusMessage = "Nothing copied yet (select a widget, Ctrl+C)";
            return;
        }

        PushUndo();
        DisplayWidgetDto copy = CloneWidget(clipboard_);
        copy.Id = "w" + Guid.NewGuid().ToString("N")[..8];
        copy.X += 16;
        copy.Y += 16;
        document_.Widgets.Add(copy);
        ReloadSurface();
        SelectWidgetById(copy.Id);
        StatusMessage = $"Pasted {copy.Type} as {copy.Id}";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateSelected()
    {
        if (SelectedWidget is null)
        {
            return;
        }

        CopySelected();
        Paste();
    }

    /// <summary>Selects a widget after add/paste so "Choose tag…" is one click away.</summary>
    private void SelectWidgetById(string id)
    {
        WidgetViewModelBase? widget = Surface.Widgets.FirstOrDefault(
            candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (widget is not null)
        {
            Surface.SelectWidget(widget);
        }
    }

    public void Nudge(double dx, double dy)
    {
        if (SelectedWidget is not { } widget)
        {
            return;
        }

        Surface.MoveWidgetTo(widget, widget.X + dx, widget.Y + dy);
    }

    // ---- Alignment / z-order (relative to canvas) ----

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AlignLeft() => WithSelection(w => w.X = 0);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AlignCenterX() => WithSelection(w => w.X = Math.Round((Surface.CanvasWidth - w.Width) / 2));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AlignRight() => WithSelection(w => w.X = Math.Max(0, Surface.CanvasWidth - w.Width));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AlignTop() => WithSelection(w => w.Y = 0);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AlignCenterY() => WithSelection(w => w.Y = Math.Round((Surface.CanvasHeight - w.Height) / 2));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AlignBottom() => WithSelection(w => w.Y = Math.Max(0, Surface.CanvasHeight - w.Height));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RaiseZ() => WithSelection(w => w.Z++);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void LowerZ() => WithSelection(w => w.Z = Math.Max(0, w.Z - 1));

    private void WithSelection(Action<WidgetViewModelBase> mutate)
    {
        if (SelectedWidget is not { } widget)
        {
            return;
        }

        PushUndo();
        mutate(widget);
        StatusMessage = $"{widget.Id} updated";
    }

    // ---- Selected-widget property panel ----

    public bool SelectedIsTextLabel =>
        SelectedWidget is LabelWidgetViewModel or PushButtonWidgetViewModel
            or NumericWidgetViewModel or QualityLampWidgetViewModel or BoolIndicatorWidgetViewModel;

    public string SelectedText
    {
        get => SelectedWidget switch
        {
            LabelWidgetViewModel w => w.Text,
            PushButtonWidgetViewModel w => w.Text,
            NumericWidgetViewModel w => w.Label,
            QualityLampWidgetViewModel w => w.Label,
            BoolIndicatorWidgetViewModel w => w.Label,
            _ => string.Empty
        };
        set
        {
            if (SelectedWidget is not { } widget)
            {
                return;
            }

            PushUndo();
            widget.SetText(value);
        }
    }

    public bool IsNumericWidget => SelectedWidget is NumericWidgetViewModel;

    public string SelectedUnitSource
    {
        get => (SelectedWidget as NumericWidgetViewModel)?.UnitSource ?? "manual";
        set
        {
            if (SelectedWidget is not NumericWidgetViewModel widget) return;
            PushUndo();
            widget.Props["unitSource"] = System.Text.Json.JsonSerializer.SerializeToElement(value);
            OnPropertyChanged(nameof(SelectedUnitSource));
            OnPropertyChanged(nameof(ShowManualUnit));
        }
    }

    public bool ShowManualUnit => SelectedUnitSource != "server";

    public string SelectedUnit
    {
        get => (SelectedWidget as NumericWidgetViewModel)?.Unit ?? string.Empty;
        set
        {
            if (SelectedWidget is not NumericWidgetViewModel widget) return;
            PushUndo();
            widget.Props["unit"] = System.Text.Json.JsonSerializer.SerializeToElement(value ?? string.Empty);
        }
    }

    // ---- Store ----

    [RelayCommand]
    private async Task RefreshListAsync()
    {
        try
        {
            store_.SetBaseAddress(StoreUrl);
            DisplayListResponse list = await store_.ListAsync(CancellationToken.None).ConfigureAwait(true);
            ExistingDisplays.Clear();
            foreach (DisplayListItemDto item in list.Items)
            {
                ExistingDisplays.Add(item);
            }

            StatusMessage = $"Listed {ExistingDisplays.Count} display(s)";
        }
        catch (Exception ex)
        {
            StatusMessage = "List failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        if (SelectedExisting is null)
        {
            StatusMessage = "Select a display from the list";
            return;
        }

        try
        {
            store_.SetBaseAddress(StoreUrl);
            DisplayDocumentDto? doc = await store_.GetAsync(SelectedExisting.Id, CancellationToken.None)
                .ConfigureAwait(true);
            if (doc is null)
            {
                StatusMessage = "Not found";
                return;
            }

            PushUndo();
            document_ = doc;
            DocumentId = doc.Id;
            DocumentName = doc.Name;
            DocumentVersion = doc.Version;
            ReloadSurface();
            StatusMessage = $"Opened {doc.Id} v{doc.Version}";
        }
        catch (Exception ex)
        {
            StatusMessage = "Open failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            store_.SetBaseAddress(StoreUrl);
            SyncDocumentFromSurface();
            document_.Id = DocumentId.Trim();
            document_.Name = string.IsNullOrWhiteSpace(DocumentName) ? document_.Id : DocumentName.Trim();
            document_.Version = DocumentVersion;
            document_.SchemaVersion = 1;
            if (document_.Width <= 0) document_.Width = 1920;
            if (document_.Height <= 0) document_.Height = 1080;

            (DisplayDocumentDto? saved, int status, string? error, int? currentVersion) =
                await store_.PutAsync(document_.Id, document_, CancellationToken.None).ConfigureAwait(true);

            if (status == 409)
            {
                StatusMessage = $"Version conflict (server v{currentVersion}). Open again, then Save.";
                return;
            }

            if (saved is null)
            {
                StatusMessage = error ?? ("Save failed HTTP " + status);
                return;
            }

            document_ = saved;
            DocumentVersion = saved.Version;
            DocumentId = saved.Id;
            DocumentName = saved.Name;
            ReloadSurface();
            await RefreshListAsync().ConfigureAwait(true);
            StatusMessage = $"Saved {saved.Id} v{saved.Version}";
        }
        catch (Exception ex)
        {
            StatusMessage = "Save failed: " + ex.Message;
        }
    }

    // ---- Document <-> surface sync ----

    private DisplayWidgetDto? FindDto(string id)
        => document_.Widgets.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));

    private static DisplayWidgetDto CloneWidget(DisplayWidgetDto? widget)
    {
        if (widget is null)
        {
            return new DisplayWidgetDto();
        }

        string json = JsonSerializer.Serialize(widget);
        return JsonSerializer.Deserialize<DisplayWidgetDto>(json) ?? new DisplayWidgetDto();
    }

    private void SyncDocumentFromSurface()
    {
        document_.Widgets = Surface.ExportWidgetDtos().ToList();
        document_.Width = (int)Math.Max(1, Surface.CanvasWidth);
        document_.Height = (int)Math.Max(1, Surface.CanvasHeight);
        document_.Id = DocumentId.Trim();
        document_.Name = string.IsNullOrWhiteSpace(DocumentName) ? document_.Id : DocumentName.Trim();
    }

    private void ReloadSurface()
    {
        // Work on a clone so Surface.Load mutations don't surprise us.
        string json = JsonSerializer.Serialize(document_);
        DisplayDocumentDto clone = JsonSerializer.Deserialize<DisplayDocumentDto>(json) ?? document_;
        Surface.Load(clone);
        Surface.ApplyDesignMode(true);
        SelectedWidget = Surface.SelectedWidget;
    }

    private static DisplayDocumentDto NewDocument() => new()
    {
        SchemaVersion = 1,
        Id = "plant-overview",
        Name = "Plant Overview",
        Version = 0,
        Width = 1920,
        Height = 1080,
        Widgets = new List<DisplayWidgetDto>()
    };

    public void Dispose()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;
        lifetime_.Cancel();
        lifetime_.Dispose();
        live_.StateChanged -= OnLiveStateChanged;
        _ = DisposeLiveAsync();
        bridge_.Dispose();
        store_.Dispose();
    }

    private async Task DisposeLiveAsync()
    {
        try
        {
            await live_.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Shutdown path: a link that cannot be torn down must not fault the process.
        }
    }
}
