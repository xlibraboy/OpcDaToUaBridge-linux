using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using OpcBridge.Hmi.Designer.ViewModels;

namespace OpcBridge.Hmi.Designer.Views;

public partial class DesignerWindow : Window
{
    public DesignerWindow()
    {
        InitializeComponent();
        var vm = new DesignerViewModel();
        DataContext = vm;
        Closed += (_, _) => vm.Dispose();
    }

    private DesignerViewModel? ViewModel => DataContext as DesignerViewModel;

    private bool IsTypingInTextBox
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.StatusMessage = "Pick a palette type, Add widget, drag to move, corner handle to resize. "
                + "Del deletes, Ctrl+C/V/D copy/paste/duplicate, Ctrl+Z/Y undo/redo, Ctrl+S saves, Ctrl+G toggles snap.";
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            base.OnKeyDown(e);
            return;
        }

        bool inTextBox = IsTypingInTextBox;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (ctrl)
        {
            // Let native text-box editing keep Ctrl+Z/C/V/X while typing.
            switch (e.Key)
            {
                case Key.Z when !inTextBox:
                    vm.UndoCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Y when !inTextBox:
                    vm.RedoCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.S:
                    _ = vm.SaveCommand.ExecuteAsync(null);
                    e.Handled = true;
                    break;
                case Key.C when !inTextBox:
                    vm.CopySelectedCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.V when !inTextBox:
                    vm.PasteCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.D when !inTextBox:
                    vm.DuplicateSelectedCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.N when !inTextBox:
                    vm.NewDisplayCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.O when !inTextBox:
                    _ = vm.OpenSelectedCommand.ExecuteAsync(null);
                    e.Handled = true;
                    break;
                case Key.G:
                    vm.SnapEnabled = !vm.SnapEnabled;
                    e.Handled = true;
                    break;
            }
        }
        else if (!inTextBox)
        {
            double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 8 : 1;
            switch (e.Key)
            {
                case Key.Delete:
                    vm.DeleteSelectedCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Left:
                    vm.Nudge(-step, 0);
                    e.Handled = true;
                    break;
                case Key.Right:
                    vm.Nudge(step, 0);
                    e.Handled = true;
                    break;
                case Key.Up:
                    vm.Nudge(0, -step);
                    e.Handled = true;
                    break;
                case Key.Down:
                    vm.Nudge(0, step);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    vm.Surface.SelectWidget(null);
                    e.Handled = true;
                    break;
                case Key.F5:
                    _ = vm.RefreshListCommand.ExecuteAsync(null);
                    e.Handled = true;
                    break;
            }
        }

        if (!e.Handled)
        {
            base.OnKeyDown(e);
        }
    }
}
