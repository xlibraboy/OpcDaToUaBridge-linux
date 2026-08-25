using Avalonia.Controls;
using Avalonia.Input;
using OpcBridge.Hmi.ViewModels;

namespace OpcBridge.Hmi.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        vm.SetOwnerWindow(this);
        DataContext = vm;
        Closed += async (_, _) =>
        {
            if (DataContext is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(true);
            }
        };
    }

    private void OnTagDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.OpenFaceplateCommand.CanExecute(null))
        {
            vm.OpenFaceplateCommand.Execute(null);
        }
    }
}
