using OpcBridge.Core;

namespace OpcBridge.Da;

public interface ISourceClient : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<BridgeValue>> ReadAsync(
        IReadOnlyList<TagMapping> mappings,
        CancellationToken cancellationToken);

    Task<bool> WriteAsync(string itemId, object? value, CancellationToken cancellationToken);

    bool TryGetTagMetadata(string itemId, out short? canonicalDataType, out int? accessRights);
}
