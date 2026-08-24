namespace OpcBridge.Ua;

/// <summary>Live snapshot of one subscription bucket on a connected UA source (spec §6).</summary>
public sealed record UaSubscriptionStatus(
    string BucketKey,
    int RequestedPublishingIntervalMs,
    double ActualPublishingIntervalMs,
    int ItemCount,
    bool Created);
