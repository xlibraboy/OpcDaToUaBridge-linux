namespace OpcBridge.App;

/// <summary>
/// A worker process was created but never became usable: its pipe never connected, or the
/// bootstrap/shutdown plumbing failed first. Distinct from the errors the identity layer
/// throws before any process exists (missing password, logon failure, CreateProcessAsUser) —
/// those are configuration or permission problems the operator must fix and they fault the
/// source. A start failure is treated like a crash: retried with the worker backoff and
/// quarantined after repeated attempts.
/// </summary>
internal sealed class WorkerStartException : Exception
{
    public WorkerStartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
