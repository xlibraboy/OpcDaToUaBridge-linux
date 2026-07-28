using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;
using OpcBridge.Hmi.ViewModels.Widgets;

namespace OpcBridge.Hmi.ViewModels;

public partial class DisplaySurfaceViewModel : ObservableObject
{
    private readonly MultiBridgeTagCache cache_;
    private readonly Action<TagBindingKey> openFaceplate_;
    private readonly Func<TagBindingKey, object?, Task<(bool Ok, string? Error)>> writeAsync_;

    public DisplaySurfaceViewModel(
        MultiBridgeTagCache cache,
        Action<TagBindingKey> openFaceplate,
        Func<TagBindingKey, object?, Task<(bool Ok, string? Error)>> writeAsync)
    {
        cache_ = cache;
        openFaceplate_ = openFaceplate;
        writeAsync_ = writeAsync;
    }

    [ObservableProperty]
    private string _displayId = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private double _canvasWidth = 1920;

    [ObservableProperty]
    private double _canvasHeight = 1080;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasDocument;

    public ObservableCollection<WidgetViewModelBase> Widgets { get; } = new();

    public void Clear()
    {
        Widgets.Clear();
        DisplayId = string.Empty;
        DisplayName = string.Empty;
        HasDocument = false;
        StatusMessage = string.Empty;
    }

    public void Load(DisplayDocumentDto document)
    {
        string? issue = DisplayDocumentValidator.DescribeLoadIssue(document);
        if (issue is not null)
        {
            Clear();
            StatusMessage = issue;
            return;
        }

        Widgets.Clear();
        DisplayId = document.Id;
        DisplayName = document.Name;
        CanvasWidth = document.Width;
        CanvasHeight = document.Height;
        HasDocument = true;
        StatusMessage = string.Empty;

        foreach (DisplayWidgetDto widget in document.Widgets
                     .OrderBy(w => w.Z)
                     .ThenBy(w => w.Id, StringComparer.OrdinalIgnoreCase))
        {
            Widgets.Add(WidgetViewModelBase.Create(widget, cache_, openFaceplate_, writeAsync_));
        }
    }

    public void RefreshLiveValues()
    {
        foreach (WidgetViewModelBase widget in Widgets)
        {
            widget.RefreshFromCache();
        }
    }
}
