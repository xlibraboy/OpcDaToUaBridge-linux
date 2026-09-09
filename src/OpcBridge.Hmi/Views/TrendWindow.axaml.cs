using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OpcBridge.Hmi.ViewModels;

namespace OpcBridge.Hmi.Views;

public partial class TrendWindow : Window
{
    public TrendWindow()
    {
        InitializeComponent();
        Closed += async (_, _) =>
        {
            if (DataContext is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(true);
            }
        };
    }

    public TrendWindow(TrendWindowViewModelBase viewModel)
        : this()
    {
        DataContext = viewModel;

        // Drag on the plot selects a time range → reload history for that window.
        TrendChart.ZoomRequested += (_, e) =>
        {
            _ = viewModel.ZoomToAsync(e.FromUtc, e.ToUtc);
        };

        // Double-click on the plot zooms back out to the base range.
        TrendChart.ZoomResetRequested += (_, _) =>
        {
            viewModel.ResetZoomCommand.Execute(null);
        };

        // Keep the pinned blue cursor in sync both ways (Ctrl+Click on the plot,
        // "Clear pin" button on the toolbar).
        TrendChart.PinnedAtUtc = viewModel.PinnedAtUtc;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TrendWindowViewModelBase.PinnedAtUtc))
            {
                TrendChart.PinnedAtUtc = viewModel.PinnedAtUtc;
            }
        };
        TrendChart.PinChanged += (_, pinned) =>
        {
            viewModel.PinnedAtUtc = pinned;
        };
    }

    /// <summary>Pen-table swatch click: cycle that pen's trace color.</summary>
    public void OnPenSwatchClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not TrendWindowViewModelBase viewModel || sender is not Border { Tag: string penName })
        {
            return;
        }

        switch (viewModel)
        {
            case TrendGroupViewModel group:
                group.CyclePenColor(penName);
                break;
            case TrendViewModel single:
                single.Pen.CycleColor();
                break;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Pen-table row click: emphasize that pen's trace (thick line, others thin).
    /// Ctrl+click toggles pens additively so several traces stay thick at once.
    /// </summary>
    public void OnPenRowClick(object? sender, PointerPressedEventArgs e)
    {
        // Let inner controls (checkbox, textboxes, swatch) keep their own clicks.
        if (e.Handled ||
            DataContext is not TrendWindowViewModelBase viewModel ||
            sender is not Border { Tag: string penName })
        {
            return;
        }

        viewModel.SelectPen(penName, additive: e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control));
        e.Handled = true;
    }

    /// <summary>Click on the table's empty area (header/whitespace): clear the emphasis.</summary>
    public void OnPenTableBackgroundClick(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || DataContext is not TrendWindowViewModelBase viewModel)
        {
            return;
        }

        viewModel.ClearPenSelection();
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TrendWindowViewModelBase viewModel)
        {
            return;
        }

        var options = new FilePickerSaveOptions
        {
            Title = "Export trend",
            SuggestedFileName = "trend.csv",
            DefaultExtension = "csv",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV files") { Patterns = new[] { "*.csv" } }
            }
        };
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(options).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        string? localPath = file.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            await viewModel.ExportCsvAsync(localPath).ConfigureAwait(true);
            return;
        }

        // Non-local storage (e.g. web): write the CSV text straight to the picked file.
        await using Stream stream = await file.OpenWriteAsync().ConfigureAwait(true);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(viewModel.BuildCsv()).ConfigureAwait(true);
        viewModel.StatusMessage = "Exported trend CSV";
    }

    private void OnCustomRangeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TrendWindowViewModelBase viewModel)
        {
            return;
        }

        new TrendTimeRangeWindow(viewModel).ShowDialog(this);
    }
}
