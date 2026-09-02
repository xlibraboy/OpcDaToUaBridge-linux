using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
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

    private readonly DisplayStoreClient store_ = new();
    private readonly MultiBridgeTagCache cache_ = new();
    private readonly List<string> undoStack_ = new();
    private readonly List<string> redoStack_ = new();
    private DisplayWidgetDto? clipboard_;
    private DisplayDocumentDto document_ = NewDocument();

    public DesignerViewModel()
    {
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
        string? found = await LocalBridgeDetector.DetectAsync().ConfigureAwait(true);
        if (found is null)
        {
            StatusMessage = "Local OpcBridge not detected — using " + StoreUrl;
            return;
        }

        string current = StoreUrl.Trim().TrimEnd('/');
        if (current is "" or "http://127.0.0.1:8080" or "http://localhost:8080")
        {
            StoreUrl = found;
            StatusMessage = $"Local OpcBridge detected at {found}";
        }
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
    private string _bridgeId = "default";

    [ObservableProperty]
    private string _sourceId = "default";

    [ObservableProperty]
    private string _daItemId = string.Empty;

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
        StageBindingFromSelection();
        NotifySelectionCommands();
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
        OnPropertyChanged(nameof(StagingBindingBridgeId));
        OnPropertyChanged(nameof(StagingBindingSourceId));
        OnPropertyChanged(nameof(StagingBindingDaItemId));
        OnPropertyChanged(nameof(SelectedIsTextLabel));
        ApplySelectedBindingCommand.NotifyCanExecuteChanged();
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
            if (!string.IsNullOrWhiteSpace(DaItemId))
            {
                widget.Binding = new TagBindingDto
                {
                    BridgeId = BridgeId,
                    SourceId = SourceId,
                    DaItemId = DaItemId
                };
            }

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
        StatusMessage = $"Added {type} ({widget.Id})";
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

    // Staged binding fields, applied with one click (avoids per-keystroke rebuilds).

    [ObservableProperty]
    private string _stagingBindingBridgeId = string.Empty;

    [ObservableProperty]
    private string _stagingBindingSourceId = string.Empty;

    [ObservableProperty]
    private string _stagingBindingDaItemId = string.Empty;

    private void StageBindingFromSelection()
    {
        TagBindingKey? binding = SelectedWidget?.Binding;
        StagingBindingBridgeId = binding?.BridgeId ?? string.Empty;
        StagingBindingSourceId = binding?.SourceId ?? string.Empty;
        StagingBindingDaItemId = binding?.DaItemId ?? string.Empty;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ApplySelectedBinding()
    {
        if (SelectedWidget is not { } widget || widget.Type == DisplayWidgetTypes.Label)
        {
            return;
        }

        PushUndo();
        if (string.IsNullOrWhiteSpace(StagingBindingBridgeId) && string.IsNullOrWhiteSpace(StagingBindingDaItemId))
        {
            widget.UpdateBinding(null);
            StatusMessage = $"{widget.Id} unbound";
            return;
        }

        widget.UpdateBinding(TagBindingKey.Create(
            string.IsNullOrWhiteSpace(StagingBindingBridgeId) ? "default" : StagingBindingBridgeId.Trim(),
            string.IsNullOrWhiteSpace(StagingBindingSourceId) ? "default" : StagingBindingSourceId.Trim(),
            StagingBindingDaItemId.Trim()));
        StatusMessage = $"{widget.Id} bound to {widget.Binding}";
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

    public void Dispose() => store_.Dispose();
}
