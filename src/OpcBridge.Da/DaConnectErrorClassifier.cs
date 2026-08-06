using System.Runtime.InteropServices;

namespace OpcBridge.Da;

/// <summary>
/// Classifies OPC DA server-activation failures for the coordinator's reconnect logic.
/// Remote activation failures (server down, network gone, DCOM refused) are transient —
/// the coordinator retries with backoff via <see cref="SourceConnectionLostException"/>.
/// Local configuration errors (ProgID not registered on this machine, logon failure)
/// are terminal and surface as Faulted.
/// </summary>
internal static class DaConnectErrorClassifier
{
    /// <summary>COM HRESULT 0x80040154: class not registered — a configuration error.</summary>
    private const int ClassNotRegistered = unchecked((int)0x80040154);

    public static bool IsRetryable(Exception exception, bool isRemote)
    {
        // Remote type lookup failures (host unreachable) are transient. A LOCAL
        // lookup failure means the ProgID is not registered on this machine — the
        // caller raises it as InvalidOperationException (terminal, Faulted).
        return isRemote && IsActivationRetryable(exception);
    }

    /// <summary>
    /// Server activation (CreateInstance) failed. A registered-but-unlaunchable server
    /// (killed process, crash on start, RPC dead) is transient — the coordinator retries
    /// with backoff. Only an explicit "class not registered" is a hard configuration error.
    /// </summary>
    public static bool IsActivationRetryable(Exception exception)
    {
        return exception is not COMException com || com.HResult != ClassNotRegistered;
    }
}
