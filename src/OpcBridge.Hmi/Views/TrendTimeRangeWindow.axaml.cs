using Avalonia.Controls;
using Avalonia.Interactivity;
using OpcBridge.Hmi.ViewModels;

namespace OpcBridge.Hmi.Views;

public partial class TrendTimeRangeWindow : Window
{
    private readonly TrendWindowViewModelBase? viewModel_;

    // Parameterless ctor exists for the XAML designer/runtime loader; interactive use
    // goes through the view-model ctor below.
    public TrendTimeRangeWindow()
    {
        InitializeComponent();
    }

    public TrendTimeRangeWindow(TrendWindowViewModelBase viewModel)
        : this()
    {
        viewModel_ = viewModel;

        // Seed the pickers with the currently displayed window (local time).
        DateTime fromLocal = viewModel.FromUtc.ToLocalTime();
        DateTime toLocal = viewModel.ToUtc.ToLocalTime();
        FromDate.SelectedDate = new DateTimeOffset(fromLocal);
        FromTime.SelectedTime = fromLocal.TimeOfDay;
        ToDate.SelectedDate = new DateTimeOffset(toLocal);
        ToTime.SelectedTime = toLocal.TimeOfDay;
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        DateTime from = Combine(FromDate, FromTime);
        DateTime to = Combine(ToDate, ToTime);
        if (to <= from)
        {
            ErrorText.Text = "The 'To' time must be after the 'From' time.";
            return;
        }

        if (viewModel_ is not null)
        {
            _ = viewModel_.ZoomToAsync(from.ToUniversalTime(), to.ToUniversalTime());
        }

        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private static DateTime Combine(DatePicker date, TimePicker time)
    {
        DateTimeOffset day = date.SelectedDate ?? DateTimeOffset.Now;
        TimeSpan clock = time.SelectedTime ?? TimeSpan.Zero;
        return day.Date + clock;
    }
}