using Avalonia.Controls;
using Avalonia.Input;
using OpcBridge.Hmi.ViewModels;

namespace OpcBridge.Hmi.Views;

public partial class FaceplateWindow : Window
{
    public FaceplateWindow()
    {
        InitializeComponent();

        // On a writeable tag the write box is the task: open with it focused. Read-only
        // faceplates keep the default focus, so Tab still walks the chrome first.
        Opened += (_, _) =>
        {
            if (DataContext is FaceplateViewModel { Writeable: true })
            {
                WriteBox.Focus();
            }
        };

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
