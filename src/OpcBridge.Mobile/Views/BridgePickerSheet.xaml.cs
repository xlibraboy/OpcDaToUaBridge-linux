using Android.Content;
using Android.Views.InputMethods;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

/// <summary>
/// The themed saved-bridge picker: the scrim + bottom sheet that replaces the platform action
/// sheet, so choosing a bridge keeps the app's own palette. One sheet, two entry points — the
/// Sign-in bridge row and the Logic bridge chip — with the same list and the same outcome: a
/// tap on a row activates that bridge, while "New bridge…" is left to the hosting page, since
/// adding one lives on Sign in (its form) and on Settings.
/// </summary>
public partial class BridgePickerSheet : ContentView
{
    private TaskCompletionSource<BridgeConnection?>? pending_;
    private IReadOnlyList<BridgeConnection> bridges_ = [];

    public BridgePickerSheet()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the sheet is asked for a new bridge; the hosting page decides where that lives.</summary>
    public event Action? NewBridgeRequested;

    /// <summary>True while the sheet covers the page; Android's back button dismisses instead of navigating.</summary>
    public bool IsOpen => overlayRoot.IsVisible;

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        // Code-painted rows do not follow a theme switch on their own.
        ThemeManager.Changed -= OnThemeChanged;
        ThemeManager.Changed += OnThemeChanged;
    }

    protected override void OnHandlerChanging(HandlerChangingEventArgs args)
    {
        base.OnHandlerChanging(args);
        if (args.NewHandler is null)
        {
            ThemeManager.Changed -= OnThemeChanged;
        }
    }

    /// <summary>
    /// Opens the sheet over the page and completes with the bridge the operator chose, or null
    /// for a dismissal (scrim, X, Cancel, or a newer open replacing this one).
    /// </summary>
    public Task<BridgeConnection?> ShowAsync(IReadOnlyList<BridgeConnection> bridges)
    {
        pending_?.TrySetResult(null);
        pending_ = new TaskCompletionSource<BridgeConnection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridges_ = bridges;
        RebuildRows();
        CloseKeyboard();
        overlayRoot.IsVisible = true;
        overlayRoot.Opacity = 0;
        sheetPanel.TranslationY = SlideDistance();
        _ = overlayRoot.FadeTo(1, 150);
        _ = sheetPanel.TranslateTo(0, 0, 220, Easing.CubicOut);
        return pending_.Task;
    }

    /// <summary>
    /// The soft keyboard belongs to whichever entry holds focus, and it would otherwise cover
    /// the sheet; releasing that focus as the sheet opens sends the keyboard away.
    /// </summary>
    private void CloseKeyboard()
    {
        Element? root = this;
        while (root.Parent is { } parent)
        {
            root = parent;
        }

        ReleaseFocus(root);
        // Clearing focus alone can leave Android's IME on screen; the window is told to hide it.
        Android.App.Activity? activity = Platform.CurrentActivity;
        Android.Views.View? decorView = activity?.Window?.DecorView;
        if (activity is null || decorView?.WindowToken is null)
        {
            return;
        }

        if (activity.GetSystemService(Context.InputMethodService) is InputMethodManager inputManager)
        {
            inputManager.HideSoftInputFromWindow(decorView.WindowToken, HideSoftInputFlags.None);
        }
    }

    private static void ReleaseFocus(Element element)
    {
        if (element is VisualElement { IsFocused: true } focused)
        {
            focused.Unfocus();
            return;
        }

        if (element is not IVisualTreeElement container)
        {
            return;
        }

        foreach (IVisualTreeElement child in container.GetVisualChildren())
        {
            if (child is Element childElement)
            {
                ReleaseFocus(childElement);
            }
        }
    }

    /// <summary>Closes the sheet without a selection; the hosting page's back button uses this too.</summary>
    public void Dismiss() => Close(null);

    private async void Close(BridgeConnection? picked)
    {
        if (!overlayRoot.IsVisible)
        {
            return;
        }

        TaskCompletionSource<BridgeConnection?>? pending = pending_;
        pending_ = null;
        _ = sheetPanel.TranslateTo(0, SlideDistance(), 180, Easing.CubicIn);
        await overlayRoot.FadeTo(0, 180);
        if (pending_ is not null)
        {
            // A newer open superseded this close; leave the sheet on screen for it.
            overlayRoot.Opacity = 1;
            return;
        }

        overlayRoot.IsVisible = false;
        pending?.TrySetResult(picked);
    }

    private double SlideDistance() =>
        Math.Max(320, (sheetPanel.Height > 0 ? sheetPanel.Height : 400) + 20);

    /// <summary>The rows are code-painted (build-time palette lookups), so the list is rebuilt per open and per theme switch.</summary>
    private void RebuildRows()
    {
        rowsHost.Children.Clear();
        if (bridges_.Count == 0)
        {
            rowsHost.Children.Add(new Label
            {
                Text = "No saved bridges yet.",
                FontSize = 12,
                TextColor = ThemePalette.Get("MutedColor")
            });
            return;
        }

        foreach (BridgeConnection connection in bridges_)
        {
            rowsHost.Children.Add(BuildBridgeRow(connection));
        }

        rowsHost.Children.Add(BuildNewBridgeRow());
    }

    private Border BuildBridgeRow(BridgeConnection connection)
    {
        BoxView dot = new()
        {
            WidthRequest = 8,
            HeightRequest = 8,
            CornerRadius = new CornerRadius(4),
            Color = DotColor(connection),
            VerticalOptions = LayoutOptions.Center
        };
        Label name = new()
        {
            Text = connection.DisplayName,
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            TextColor = ThemePalette.Get("TextColor")
        };
        Label host = new()
        {
            Text = connection.Bridge.Url,
            FontFamily = "monospace",
            FontSize = 9.5,
            TextColor = ThemePalette.Get("MutedColor"),
            LineBreakMode = LineBreakMode.TailTruncation
        };
        View trailing = connection.IsActive ? BuildActivePill() : BuildRowChevron();
        VerticalStackLayout textStack = new() { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        textStack.Add(name);
        textStack.Add(host);
        Grid rowContent = new()
        {
            ColumnDefinitions = new ColumnDefinitionCollection(
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)),
            ColumnSpacing = 10
        };
        rowContent.Add(dot);
        rowContent.Add(textStack, 1, 0);
        trailing.SetValue(Grid.ColumnProperty, 2);
        rowContent.Add(trailing);

        Border row = new()
        {
            BackgroundColor = ThemePalette.Get("SurfaceAltColor"),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
            Padding = 12,
            BindingContext = connection
        };
        row.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => Close(connection))
        });
        SemanticProperties.SetDescription(row, "Switch to " + connection.DisplayName);
        row.Content = rowContent;
        return row;
    }

    private View BuildActivePill()
    {
        Label label = new()
        {
            Text = "ACTIVE",
            FontSize = 9,
            FontAttributes = FontAttributes.Bold,
            CharacterSpacing = 0.8,
            TextColor = ThemePalette.Get("GoodColor"),
            VerticalOptions = LayoutOptions.Center
        };
        return new Border
        {
            BackgroundColor = ThemePalette.Get("SurfaceColor"),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(6) },
            Padding = new Thickness(8, 0),
            HeightRequest = 20,
            VerticalOptions = LayoutOptions.Center,
            Content = label
        };
    }

    private View BuildRowChevron() => new Image
    {
        Source = ImageSource.FromFile(IconName("icon_chevron_right")),
        WidthRequest = 14,
        HeightRequest = 14,
        VerticalOptions = LayoutOptions.Center
    };

    private Border BuildNewBridgeRow()
    {
        Image plus = new()
        {
            Source = ImageSource.FromFile(IconName("icon_plus")),
            WidthRequest = 14,
            HeightRequest = 14,
            VerticalOptions = LayoutOptions.Center
        };
        Label label = new()
        {
            Text = "New bridge…",
            FontSize = 13,
            TextColor = ThemePalette.Get("MutedColor"),
            VerticalOptions = LayoutOptions.Center
        };
        HorizontalStackLayout content = new() { Spacing = 10, VerticalOptions = LayoutOptions.Center };
        content.Add(plus);
        content.Add(label);
        Border row = new()
        {
            BackgroundColor = ThemePalette.Get("PageBg"),
            Stroke = ThemePalette.Get("BorderColor"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
            Padding = new Thickness(12, 0),
            HeightRequest = 44
        };
        row.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                Close(null);
                NewBridgeRequested?.Invoke();
            })
        });
        row.Content = content;
        return row;
    }

    /// <summary>The icon drawn for the surface the sheet is on, matching <c>{theme:ThemeIcon}</c>'s file pairs.</summary>
    private static string IconName(string name) =>
        name + (ThemePalette.IsLight ? "_light" : string.Empty) + ".png";

    private static Color DotColor(BridgeConnection connection) => connection switch
    {
        _ when connection.IsOffline => ThemePalette.Get("BadColor"),
        _ when connection.IsConnected => ThemePalette.Get("GoodColor"),
        _ => ThemePalette.Get("MutedColor")
    };

    private void OnDismissTapped(object? sender, EventArgs e) => Close(null);

    private void OnSheetTapped(object? sender, TappedEventArgs e)
    {
    }

    private void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (overlayRoot.IsVisible)
            {
                RebuildRows();
            }
        });
}
