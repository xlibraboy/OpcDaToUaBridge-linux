using Avalonia.Controls;
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

    public TrendWindow(TrendViewModel viewModel)
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
    }
}
