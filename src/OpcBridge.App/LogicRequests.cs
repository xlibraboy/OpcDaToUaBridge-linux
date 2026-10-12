using OpcBridge.Client;

namespace OpcBridge.App;

/// <summary>Request body for <c>POST /api/logic/blocks</c> — a full block, saved by id.</summary>
public sealed record LogicBlockSaveRequest(LogicBlockDto? Block);

/// <summary>
/// Request body for <c>POST /api/logic/relays</c> — the relay/tag bindings the editor's blocks
/// use. The whole table is replaced, like a block save replaces one block.
/// </summary>
public sealed record LogicRelaySaveRequest(List<LogicRelayDto>? Relays);
