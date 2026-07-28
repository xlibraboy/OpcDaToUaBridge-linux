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
    }
}
