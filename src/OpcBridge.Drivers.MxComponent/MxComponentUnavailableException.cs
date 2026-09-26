namespace OpcBridge.Drivers.MxComponent;

/// <summary>
/// This machine cannot run MX Component at all: the ActUtlType COM class is not registered
/// (MX Component 4 not installed) or it cannot be instantiated. Terminal by design — the
/// source is reported Faulted rather than retried, because no retry installs MX Component.
/// Classified by <see cref="MxConnectErrorClassifier"/>.
/// </summary>
internal sealed class MxComponentUnavailableException : InvalidOperationException
{
    public MxComponentUnavailableException(string message)
        : base(message)
    {
    }
}
