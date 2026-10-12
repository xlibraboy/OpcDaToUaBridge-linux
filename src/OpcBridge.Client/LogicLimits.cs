namespace OpcBridge.Client;

/// <summary>
/// The bounds the logic engine enforces on an authored network, shared so the bridge's store
/// validation, the HMI editor's indent clamp and the tests all agree on one set of numbers.
/// </summary>
public static class LogicLimits
{
    /// <summary>Deepest an IEC 61131-3 network may nest (a root is depth 0).</summary>
    public const int MaxElementDepth = 16;

    /// <summary>Most elements one block's network may hold, coils and relay contacts included.</summary>
    public const int MaxElementsPerBlock = 256;

    /// <summary>Longest internal relay name a coil or relay contact may carry (e.g. "M01").</summary>
    public const int MaxRelayNameLength = 16;
}
