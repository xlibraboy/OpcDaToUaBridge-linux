namespace OpcBridge.App;

public enum InterlinkHealth
{
    Flowing,
    Idle,
    Waiting,
    WriteFailed
}

/// <summary>Inputs for deriving one link's runtime health. Pure data so the derivation stays unit-testable.</summary>
public sealed record InterlinkStatusInput(
    bool Enabled,
    bool ProviderHasValue,
    bool ProviderGood,
    bool ConsumerSourceConnected,
    long Attempts,
    long Failures,
    DateTime? LastForwardUtc,
    bool? LastWriteSuccess,
    string? LastError,
    DateTime NowUtc);

/// <summary>Immutable snapshot of one link's forwarding telemetry.</summary>
public sealed record InterlinkStats(
    long Attempts,
    long Successes,
    long Failures,
    DateTime? LastForwardUtc,
    bool? LastWriteSuccess,
    string? LastError)
{
    public static InterlinkStats Empty { get; } = new(0, 0, 0, null, null, null);
}

/// <summary>
/// Derives an interlink's runtime health from its endpoints' live state and its
/// forwarding telemetry. Priority: structural problems (disabled / disconnected /
/// bad quality) first, then a failed write, then recent successful flow, else idle.
/// </summary>
public static class InterlinkStatusEvaluator
{
    /// <summary>A successful forward within this window means the link is actively flowing.</summary>
    public static readonly TimeSpan FlowWindow = TimeSpan.FromSeconds(30);

    public static InterlinkHealth Derive(in InterlinkStatusInput input, out string? reason)
    {
        if (!input.Enabled)
        {
            reason = "link is disabled";
            return InterlinkHealth.Waiting;
        }

        if (!input.ConsumerSourceConnected)
        {
            reason = "consumer source disconnected";
            return InterlinkHealth.Waiting;
        }

        if (!input.ProviderHasValue)
        {
            reason = "no provider value yet";
            return InterlinkHealth.Waiting;
        }

        if (!input.ProviderGood)
        {
            reason = "provider value bad quality";
            return InterlinkHealth.Waiting;
        }

        if (input.LastWriteSuccess == false)
        {
            reason = string.IsNullOrWhiteSpace(input.LastError) ? "last write failed" : input.LastError;
            return InterlinkHealth.WriteFailed;
        }

        if (input.LastForwardUtc.HasValue &&
            input.LastWriteSuccess != false &&
            input.NowUtc - input.LastForwardUtc.Value <= FlowWindow)
        {
            reason = null;
            return InterlinkHealth.Flowing;
        }

        reason = "no recent provider changes";
        return InterlinkHealth.Idle;
    }
}
