using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
        // Right-drag on the plot selects a time range → reload history for that window.
        TrendChart.ZoomRequested += (_, e) =>
        {
            _ = viewModel.ZoomToAsync(e.FromUtc, e.ToUtc);
        };

        // Double-click on the plot zooms back out to the base range.
        TrendChart.ZoomResetRequested += (_, _) =>
        {
            viewModel.ResetZoomCommand.Execute(null);
        };

        // Legend interaction (group trends): swatch click cycles color, row click toggles visibility.
        if (viewModel is TrendGroupViewModel group)
        {
            TrendChart.LegendColorRequested += (_, e) => group.CycleSeriesColor(e.SeriesName);
            TrendChart.LegendVisibilityRequested += (_, e) => group.ToggleSeriesVisibility(e.SeriesName);
        }
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
