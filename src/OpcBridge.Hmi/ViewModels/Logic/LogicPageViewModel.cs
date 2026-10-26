using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// The Logic page: the bridge selector and the selected bridge's own <c>/logic</c> page. The
/// page is served by the bridge, so it is same-origin with <c>/api/logic*</c> and the
/// <c>/hmi</c> hub; the runtime embeds it with WebView2 on Windows and opens it in the system
/// browser elsewhere.
/// </summary>
public sealed partial class LogicPageViewModel : ObservableObject
{
    private readonly BridgeConnectionManager connections_;

    public LogicPageViewModel(BridgeConnectionManager connections)
    {
        connections_ = connections;
        RefreshBridges();
    }

    // ---- Bridge selector ----

    public ObservableCollection<LogicBridgeOption> Bridges { get; } = new();

    [ObservableProperty]
    private LogicBridgeOption? _selectedBridge;

    /// <summary>The selected bridge's own logic page. Empty while no bridge is selected.</summary>
    public string WebPageUrl => SelectedBridge?.PageUrl ?? string.Empty;

    public bool HasWebPageUrl => WebPageUrl.Length > 0;

    public string BridgeStatusText => SelectedBridge is null
        ? "No bridge"
        : $"{SelectedBridge.Label} · {SelectedBridge.StatusText}";

    public bool IsBridgeOk => SelectedBridge is { IsConnected: true };

    public bool IsBridgeWarn => SelectedBridge?.State == BridgeLinkState.Reconnecting;

    public bool IsBridgeBad => SelectedBridge is null || SelectedBridge.State == BridgeLinkState.Failed;

    /// <summary>True while the Logic page is the page on screen; the view creates its WebView then.</summary>
    [ObservableProperty]
    private bool _isPageVisible;

    partial void OnSelectedBridgeChanged(LogicBridgeOption? value) => NotifyBridgeDerived();

    // ---- Lifecycle (driven by MainViewModel) ----

    /// <summary>Starts/stops nothing by itself; the page's view follows it to own the WebView.</summary>
    public void SetPageVisible(bool visible) => IsPageVisible = visible;

    /// <summary>Called when the connect/disconnect path changed the live sessions.</summary>
    public void OnConnectionChanged() => RefreshBridges();

    /// <summary>Called when a bridge's link state changed.</summary>
    public void OnLinkStatusesChanged() => RefreshBridges();

    private void RefreshBridges()
    {
        string? selectedId = SelectedBridge?.BridgeId;
        List<LogicBridgeOption> options = connections_.LinkStatuses
            .Select(status => new LogicBridgeOption(
                status.BridgeId,
                status.State,
                connections_.TryGetBaseUrl(status.BridgeId, out string baseUrl) ? baseUrl : string.Empty))
            .ToList();

        if (!Bridges.SequenceEqual(options))
        {
            Bridges.Clear();
            foreach (LogicBridgeOption option in options)
            {
                Bridges.Add(option);
            }
        }

        LogicBridgeOption? selection = selectedId is null
            ? null
            : Bridges.FirstOrDefault(option => string.Equals(option.BridgeId, selectedId, StringComparison.OrdinalIgnoreCase));
        selection ??= Bridges.FirstOrDefault(option => option.IsConnected) ?? Bridges.FirstOrDefault();
        SelectedBridge = selection;

        // A link-state change replaces the option record without moving the selection, so the
        // selected bridge's own notification does not fire; the pill still has to follow it.
        NotifyBridgeDerived();
    }

    private void NotifyBridgeDerived()
    {
        OnPropertyChanged(nameof(BridgeStatusText));
        OnPropertyChanged(nameof(IsBridgeOk));
        OnPropertyChanged(nameof(IsBridgeWarn));
        OnPropertyChanged(nameof(IsBridgeBad));
        OnPropertyChanged(nameof(WebPageUrl));
        OnPropertyChanged(nameof(HasWebPageUrl));
    }
}
