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

    [ObservableProperty]
    private bool _isDesignMode;

    [ObservableProperty]
    private WidgetViewModelBase? _selectedWidget;

    /// <summary>Grid step in px for drag snapping (design mode only; null disables).</summary>
    [ObservableProperty]
    private double? _snapStep;

    /// <summary>Whether to draw the dot-grid background (design mode only).</summary>
    [ObservableProperty]
    private bool _showGrid;

    /// <summary>Raised once per drag/resize when the first movement happens, so the
    /// designer can snapshot undo state BEFORE geometry changes.</summary>
    public event Action? EditStarted;

    public void RaiseEditStarted() => EditStarted?.Invoke();

    public ObservableCollection<WidgetViewModelBase> Widgets { get; } = new();

    public void Clear()
    {
        Widgets.Clear();
        SelectedWidget = null;
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
        SelectedWidget = null;
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
            WidgetViewModelBase vm = WidgetViewModelBase.Create(widget, cache_, openFaceplate_, writeAsync_);
            vm.IsDesignMode = IsDesignMode;
            Widgets.Add(vm);
        }
    }

    public void RefreshLiveValues()
    {
        foreach (WidgetViewModelBase widget in Widgets)
        {
            widget.RefreshFromCache();
        }
    }

    public void SelectWidget(WidgetViewModelBase? widget)
    {
        if (SelectedWidget is not null)
        {
            SelectedWidget.IsSelected = false;
        }

        SelectedWidget = widget;
        if (widget is not null)
        {
            widget.IsSelected = true;
        }
    }

    public WidgetViewModelBase? FindWidgetAt(double canvasX, double canvasY)
    {
        // Top-most first (higher Z, then later in collection).
        foreach (WidgetViewModelBase widget in Widgets
                     .OrderByDescending(w => w.Z)
                     .ThenByDescending(w => Widgets.IndexOf(w)))
        {
            if (canvasX >= widget.X && canvasX <= widget.X + widget.Width
                && canvasY >= widget.Y && canvasY <= widget.Y + widget.Height)
            {
                return widget;
            }
        }

        return null;
    }

    public void MoveWidgetTo(WidgetViewModelBase widget, double x, double y)
    {
        double step = SnapStep ?? 0;
        if (step > 1)
        {
            x = Math.Round(x / step) * step;
            y = Math.Round(y / step) * step;
        }

        widget.X = Math.Max(0, Math.Min(CanvasWidth - Math.Max(8, widget.Width), x));
        widget.Y = Math.Max(0, Math.Min(CanvasHeight - Math.Max(8, widget.Height), y));
    }

    public void ResizeWidgetTo(WidgetViewModelBase widget, double width, double height)
    {
        double step = SnapStep ?? 0;
        if (step > 1)
        {
            width = Math.Round(width / step) * step;
            height = Math.Round(height / step) * step;
        }

        widget.Width = Math.Max(24, Math.Min(CanvasWidth - widget.X, width));
        widget.Height = Math.Max(24, Math.Min(CanvasHeight - widget.Y, height));
    }

    public void ApplyDesignMode(bool enabled)
    {
        IsDesignMode = enabled;
        foreach (WidgetViewModelBase widget in Widgets)
        {
            widget.IsDesignMode = enabled;
            if (!enabled)
            {
                widget.IsSelected = false;
            }
        }

        if (!enabled)
        {
            SelectedWidget = null;
        }
    }

    public IReadOnlyList<DisplayWidgetDto> ExportWidgetDtos()
    {
        return Widgets.Select(w => new DisplayWidgetDto
        {
            Id = w.Id,
            Type = w.Type,
            X = w.X,
            Y = w.Y,
            W = w.Width,
            H = w.Height,
            Z = w.Z,
            Props = new Dictionary<string, System.Text.Json.JsonElement>(w.Props),
            Binding = w.Binding is null
                ? null
                : new TagBindingDto
                {
                    BridgeId = w.Binding.Value.BridgeId,
                    SourceId = w.Binding.Value.SourceId,
                    DaItemId = w.Binding.Value.DaItemId
                }
        }).ToList();
    }
}
