using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpcBridge.Hmi.ViewModels.Logic;

namespace OpcBridge.Hmi.Views;

/// <summary>
/// The runtime's plant-logic page: the block editor and the evaluated result, per bridge.
/// Opened from View ▸ Logic (Ctrl+4). The Web page tab embeds the bridge's own /logic page
/// with WebView2 on Windows; other hosts open it in the system browser instead.
/// </summary>
public partial class LogicPage : UserControl
{
    private LogicPanelViewModel? panel_;
    private AvaloniaWebView.WebView? webView_;
    private bool webViewFailed_;

    public LogicPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            UnwirePanel();
            WirePanel();
        };
    }

    private LogicPanelViewModel? Panel => DataContext as LogicPanelViewModel;

    private void WirePanel()
    {
        if (Panel is not { } panel)
        {
            return;
        }

        panel_ = panel;
        panel.PropertyChanged += OnPanelPropertyChanged;

        // The page owns the dialogs; the view model only asks.
        panel.ConfirmAsync ??= (title, message, confirm) =>
            ConfirmWindow.ShowFor(TopLevel.GetTopLevel(this) as Window, title, message, confirm);

        // New block asks for the name, the group and the AREA first — the area is the
        // tag-sharing scope, so it is set before any tag is authored.
        panel.NewBlockPrompt ??= (owner, request) =>
            LogicNewBlockWindow.ShowFor(owner ?? TopLevel.GetTopLevel(this) as Window, request);

        ReloadWebButton.IsVisible = OperatingSystem.IsWindows();
        UpdateWebFallbackText();
        if (panel.IsWebTab)
        {
            EnsureWebView();
        }
    }

    private void UnwirePanel()
    {
        if (panel_ is { } panel)
        {
            panel.PropertyChanged -= OnPanelPropertyChanged;
        }

        panel_ = null;
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogicPanelViewModel.IsWebTab))
        {
            if (Panel is { IsWebTab: true })
            {
                EnsureWebView();
                NavigateWebView();
            }

            return;
        }

        if (e.PropertyName == nameof(LogicPanelViewModel.WebPageUrl))
        {
            NavigateWebView();
            UpdateWebFallbackText();
        }
    }

    private void EnsureWebView()
    {
        if (!OperatingSystem.IsWindows() || webView_ is not null || Panel is not { } panel)
        {
            return;
        }

        try
        {
            var view = new AvaloniaWebView.WebView();
            view.WebViewCreated += (_, e) =>
            {
                if (!e.IsSucceed)
                {
                    string message = e.Message ?? "The embedded browser could not be created.";
                    // Detach on the next dispatcher turn: disposing the control from inside its
                    // own creation callback is re-entrant.
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => FailWebView(message));
                }
            };
            view.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess)
                {
                    SetWebStatus("The page did not load. Check that the bridge is running, or open it in the browser.");
                }
            };

            webView_ = view;
            webViewFailed_ = false;
            NavigateWebView();
            WebHost.Content = view;
            WebHost.IsVisible = true;
            WebFallback.IsVisible = false;
            SetWebStatus(string.Empty);
        }
        catch (Exception ex)
        {
            FailWebView(ex.Message);
        }
    }

    private void NavigateWebView()
    {
        if (Panel is not { } panel)
        {
            return;
        }

        if (webView_ is not null && Uri.TryCreate(panel.WebPageUrl, UriKind.Absolute, out Uri? uri))
        {
            if (webView_.Url != uri)
            {
                webView_.Url = uri;
            }
        }

        UpdateWebFallbackText();
    }

    private void FailWebView(string message)
    {
        webViewFailed_ = true;
        if (webView_ is not null)
        {
            WebHost.Content = null;
            webView_ = null;
        }

        WebHost.IsVisible = false;
        WebFallback.IsVisible = true;
        ReloadWebButton.IsVisible = false;
        UpdateWebFallbackText();
        SetWebStatus(message);
    }

    private void UpdateWebFallbackText()
    {
        if (webView_ is not null && !webViewFailed_)
        {
            return;
        }

        if (Panel is not { HasWebPageUrl: true })
        {
            WebFallbackTitle.Text = "No bridge selected";
            WebFallbackMessage.Text = "Connect to a bridge and pick it above; its /logic page is shown here.";
            RetryWebButton.IsVisible = false;
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            WebFallbackTitle.Text = "Open the logic page in a browser";
            WebFallbackMessage.Text =
                "The embedded page is shown in the Windows runtime. On this host, open the bridge's /logic page in the system browser — same page, same data.";
            RetryWebButton.IsVisible = false;
            return;
        }

        if (webViewFailed_)
        {
            WebFallbackTitle.Text = "The embedded page could not start";
            WebFallbackMessage.Text =
                "The Microsoft Edge WebView2 runtime is required. Install it, then press Retry — or open the page in your browser.";
            RetryWebButton.IsVisible = true;
            return;
        }

        WebFallbackTitle.Text = "Loading the embedded page…";
        WebFallbackMessage.Text = "The bridge's /logic page is opening in the embedded browser.";
        RetryWebButton.IsVisible = false;
    }

    /// <summary>
    /// Retry recreates the control after a failed start, so installing the WebView2 runtime
    /// does not need an app restart.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RetryWebView()
    {
        webViewFailed_ = false;
        SetWebStatus(string.Empty);
        ReloadWebButton.IsVisible = OperatingSystem.IsWindows();
        WebHost.IsVisible = false;
        WebFallback.IsVisible = true;
        EnsureWebView();
    }

    private void SetWebStatus(string message)
    {
        WebStatusText.Text = message;
        WebStatusText.IsVisible = message.Length > 0;
    }

    private void OnReloadWebClick(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows())
        {
            OpenInBrowser();
            return;
        }

        if (webView_ is null)
        {
            RetryWebView();
            return;
        }

        if (!webView_.Reload())
        {
            NavigateWebView();
        }
    }

    private void OnOpenInBrowserClick(object? sender, RoutedEventArgs e) => OpenInBrowser();

    private void OpenInBrowser()
    {
        if (Panel is not { } panel
            || !Uri.TryCreate(panel.WebPageUrl, UriKind.Absolute, out Uri? uri))
        {
            SetWebStatus("Select a bridge first — its /logic page has no address yet.");
            return;
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            SetWebStatus(string.Empty);
        }
        catch (Exception ex)
        {
            SetWebStatus("Could not open the browser: " + ex.Message);
        }
    }

    private void OnBlocksClick(object? sender, RoutedEventArgs e)
    {
        if (Panel is { } panel)
        {
            LogicBlocksWindow.ShowFor(TopLevel.GetTopLevel(this) as Window, panel);
        }
    }

    /// <summary>
    /// The Tags tab's "From bridge…": the page shows the picker, the panel turns the confirmed
    /// tags into the area's named entries.
    /// </summary>
    private void OnPickTagsClick(object? sender, RoutedEventArgs e)
    {
        if (Panel is not { } panel)
        {
            return;
        }

        var viewModel = new LogicTagPickViewModel(panel.BuildTagPickRows(), panel.AreaTags.Name.Trim());
        LogicTagPickWindow.ShowFor(
            TopLevel.GetTopLevel(this) as Window,
            viewModel,
            tags => panel.AddTagsFromBridge(tags));
    }

    private void OnGuideClick(object? sender, RoutedEventArgs e) =>
        LogicGuideWindow.ShowFor(TopLevel.GetTopLevel(this) as Window);
}
