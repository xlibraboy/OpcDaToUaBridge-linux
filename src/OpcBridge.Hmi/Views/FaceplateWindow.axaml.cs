using Avalonia.Controls;
using OpcBridge.Hmi.ViewModels;

namespace OpcBridge.Hmi.Views;

public partial class FaceplateWindow : Window
{
    public FaceplateWindow()
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

    public FaceplateWindow(FaceplateViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }
}
