using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpcBridge.Hmi.ViewModels.Logic;

namespace OpcBridge.Hmi.Views;

/// <summary>
/// The runtime's plant-logic page: the bridge selector and the selected bridge's own /logic
/// page. Opened from View ▸ Logic (Ctrl+4). The page is embedded with WebView2 on Windows;
/// other hosts open it in the system browser instead.
/// </summary>
public partial class LogicPage : UserControl
{
    private LogicPageViewModel? panel_;
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

    private LogicPageViewModel? Panel => DataContext as LogicPageViewModel;

    private void WirePanel()
    {
        if (Panel is not { } panel)
        {
            return;
        }

        panel_ = panel;
        panel.PropertyChanged += OnPanelPropertyChanged;

        ReloadWebButton.IsVisible = OperatingSystem.IsWindows();
        UpdateWebFallbackText();
        if (panel.IsPageVisible)
        {
            EnsureWebView();
            NavigateWebView();
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
        if (e.PropertyName == nameof(LogicPageViewModel.IsPageVisible))
        {
            if (Panel is { IsPageVisible: true })
            {
                EnsureWebView();
                NavigateWebView();
            }

            return;
        }

        if (e.PropertyName == nameof(LogicPageViewModel.WebPageUrl))
        {
            NavigateWebView();
            UpdateWebFallbackText();
        }
    }

    private void EnsureWebView()
    {
        if (!OperatingSystem.IsWindows() || webView_ is not null || Panel is not { IsPageVisible: true })
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
}
