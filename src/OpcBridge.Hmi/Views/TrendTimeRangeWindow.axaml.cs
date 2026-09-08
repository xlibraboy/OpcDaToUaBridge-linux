using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpcBridge.Hmi.Core;
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

        // Clock indicator: pickers use this process's local zone; the trend stores UTC.
        // In containers without a TZ setting this reads UTC+00:00, which is then correct.
        TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        string offsetText = $"UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
        ClockHint.Text = $"Times are local ({offsetText}) — applied to the trend as UTC";

        // Presets are relative to the current 'To' (i.e. "now" for a live trend).
        UpdatePreview();
        FromDate.SelectedDateChanged += (_, _) => UpdatePreview();
        FromTime.SelectedTimeChanged += (_, _) => UpdatePreview();
        ToDate.SelectedDateChanged += (_, _) => UpdatePreview();
        ToTime.SelectedTimeChanged += (_, _) => UpdatePreview();
    }

    private void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        double hours = TrendRange.ParseHours(tag);
        DateTime to = DateTime.Now;
        DateTime from = to.AddHours(-hours);
        FromDate.SelectedDate = new DateTimeOffset(from);
        FromTime.SelectedTime = from.TimeOfDay;
        ToDate.SelectedDate = new DateTimeOffset(to);
        ToTime.SelectedTime = to.TimeOfDay;
        UpdatePreview();
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

        if (to - from < TimeSpan.FromMinutes(1))
        {
            ErrorText.Text = "The range must be at least one minute long.";
            return;
        }

        if (viewModel_ is not null)
        {
            _ = viewModel_.ZoomToAsync(from.ToUniversalTime(), to.ToUniversalTime());
        }

        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void UpdatePreview()
    {
        DateTime from = Combine(FromDate, FromTime);
        DateTime to = Combine(ToDate, ToTime);
        ErrorText.Text = to <= from ? "The 'To' time must be after the 'From' time." : string.Empty;
        if (to <= from)
        {
            PreviewText.Text = string.Empty;
            return;
        }

        string FormatLocal(DateTime t) => t.ToString("dd MMM HH:mm", CultureInfo.CurrentCulture);
        string FormatUtc(DateTime t) => t.ToUniversalTime().ToString("dd MMM HH:mm", CultureInfo.CurrentCulture);
        PreviewText.Text =
            $"Span: {FormatDuration(to - from)} · Local {FormatLocal(from)} → {FormatLocal(to)} · UTC {FormatUtc(from)} → {FormatUtc(to)}";
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalMinutes < 60)
        {
            return $"{(int)Math.Round(span.TotalMinutes)} min";
        }

        if (span.TotalHours < 48)
        {
            return $"{Math.Round(span.TotalHours, 1)} h";
        }

        return $"{Math.Round(span.TotalDays, 1)} days";
    }

    private static DateTime Combine(DatePicker date, TimePicker time)
    {
        DateTimeOffset day = date.SelectedDate ?? DateTimeOffset.Now;
        TimeSpan clock = time.SelectedTime ?? TimeSpan.Zero;
        return day.Date + clock;
    }
}
