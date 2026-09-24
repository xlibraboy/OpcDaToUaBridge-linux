using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Polls native process resource counters (handles, GDI objects, USER objects)
/// every 5 seconds and publishes them via <see cref="BridgeState"/>.
/// On non-Windows hosts, reports <see cref="ResourceSnapshot.Unsupported"/>.
/// </summary>
public sealed class OpcBridgeMonitor : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private readonly BridgeState bridge_state_;

    public OpcBridgeMonitor(BridgeState bridgeState)
    {
        bridge_state_ = bridgeState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            bridge_state_.UpdateResources(ResourceSnapshot.Unsupported);
            return;
        }

        using PeriodicTimer timer = new(Interval);
        do
        {
            bridge_state_.UpdateResources(Sample());
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
    [SupportedOSPlatform("windows")]
    private static ResourceSnapshot Sample()
    {
        // The Process object owns the process handle; without disposal every sample leaks one.
        using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
        IntPtr handle = process.Handle;
        int handles = GetProcessHandleCount(handle, out int handleCount) != 0
            ? handleCount
            : 0;
        int gdi = GetGuiResources(handle, GR_GDIOBJECTS);
        int user = GetGuiResources(handle, GR_USEROBJECTS);
        return new ResourceSnapshot(true, handles, gdi, user);
    }

    private const int GR_GDIOBJECTS = 0;
    private const int GR_USEROBJECTS = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetProcessHandleCount(IntPtr handle, out int pdwHandleCount);

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr hProcess, int uiFlags);
}

/// <summary>
/// Snapshot of native resource counters. <see cref="Supported"/> is false on non-Windows.
/// </summary>
public sealed record ResourceSnapshot(bool Supported, int HandleCount, int GdiObjects, int UserObjects)
{
    public static ResourceSnapshot Unsupported { get; } = new(false, 0, 0, 0);
}
