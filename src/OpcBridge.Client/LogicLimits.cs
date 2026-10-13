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

    /// <summary>Longest tags source name — the machine a block's tags are shared by (e.g. "DRYEND").</summary>
    public const int MaxTagSourceNameLength = 40;

    /// <summary>Longest name a tags source entry may carry (e.g. "START_PB").</summary>
    public const int MaxTagNameLength = 32;

    /// <summary>Most entries one machine's tags source may hold.</summary>
    public const int MaxTagsPerSource = 256;
}
