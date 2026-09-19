using Avalonia.Controls;
using Avalonia.Input;
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

    /// <summary>Escape closes the faceplate, the same way the window chrome does.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }
}
