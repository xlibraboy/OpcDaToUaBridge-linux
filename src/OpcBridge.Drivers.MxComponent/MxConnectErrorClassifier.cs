namespace OpcBridge.Drivers.MxComponent;

/// <summary>
/// Decides whether an MX Component connect failure is worth retrying (#5).
///
/// Transient failures — the logical station still held by the session we just released, a
/// momentarily unreachable PLC, an ActUtlType call that failed — are surfaced as
/// <see cref="OpcBridge.Da.SourceConnectionLostException"/> so the coordinator reports
/// "Reconnecting" and retries with backoff, exactly like the OPC DA and OPC UA drivers.
/// Without this an MX source was parked in the terminal "Faulted" state on the first failed
/// attempt and never retried, so a resume that lost a race with the COM release never recovered.
///
/// Terminal failures stay Faulted: MX Component missing or not creatable
/// (<see cref="MxComponentUnavailableException"/>), non-Windows
/// (<see cref="PlatformNotSupportedException"/>), a disposed client, and cancellation. Sibling:
/// <c>OpcBridge.Da.DaConnectErrorClassifier</c>.
/// </summary>
internal static class MxConnectErrorClassifier
{
    public static bool IsTransient(Exception exception) =>
        exception is not (MxComponentUnavailableException
            or PlatformNotSupportedException
            or ObjectDisposedException
            or OperationCanceledException);
}
