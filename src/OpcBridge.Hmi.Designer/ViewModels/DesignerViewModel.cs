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
    private readonly DisplayStoreClient store_ = new();
    private readonly MultiBridgeTagCache cache_ = new();
    private DisplayDocumentDto document_ = NewDocument();

    public DesignerViewModel()
    {
        Surface = new DisplaySurfaceViewModel(cache_, _ => { }, (_, _) => Task.FromResult((true, (string?)null)));
        Surface.ApplyDesignMode(true);
        ReloadSurface();
    }

    public DisplaySurfaceViewModel Surface { get; }

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

    [RelayCommand]
    private void NewDisplay()
    {
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
