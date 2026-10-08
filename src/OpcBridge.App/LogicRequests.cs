using OpcBridge.Client;

namespace OpcBridge.App;

/// <summary>Request body for <c>POST /api/logic/blocks</c> — a full block, saved by id.</summary>
public sealed record LogicBlockSaveRequest(LogicBlockDto? Block);
