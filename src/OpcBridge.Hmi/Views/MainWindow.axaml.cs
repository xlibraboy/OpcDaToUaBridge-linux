using Avalonia.Controls;
using Avalonia.Interactivity;
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

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private bool IsTypingInTextBox
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    private void OnTagDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.OpenFaceplateCommand.CanExecute(null))
        {
            vm.OpenFaceplateCommand.Execute(null);
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.StatusMessage = "OpcBridge HMI — SCADA operator interface for the OpcBridge (OPC DA / OPC UA / MQTT / InfluxDB).";
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            base.OnKeyDown(e);
            return;
        }

        // Skip single-key shortcuts while typing in a text field.
        if (IsTypingInTextBox && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            base.OnKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.D1 when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                vm.ShowHomeCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.D2 when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                vm.ShowConfigCommand.Execute(null);
                e.Handled = true;
                break;
        }

        if (!e.Handled)
        {
            base.OnKeyDown(e);
        }
    }
}
