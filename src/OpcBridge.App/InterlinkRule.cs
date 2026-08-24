namespace OpcBridge.App;

public sealed record InterlinkRule(
    Guid Id,
    string ProviderSourceId,
    string ProviderItemId,
    string ConsumerSourceId,
    string ConsumerItemId,
    bool Enabled,
    short? ProviderCanonicalType,
    short? ConsumerCanonicalType);