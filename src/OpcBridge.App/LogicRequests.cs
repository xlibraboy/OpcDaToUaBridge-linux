using OpcBridge.Client;

namespace OpcBridge.App;

/// <summary>Request body for <c>POST /api/logic/blocks</c> — a full block, saved by id.</summary>
public sealed record LogicBlockSaveRequest(LogicBlockDto? Block);

/// <summary>Request body for <c>POST /api/logic/tagsources</c> — one area's tags, saved by name (the wire body property stays <c>tagSource</c>).</summary>
public sealed record LogicAreaTagsSaveRequest(LogicAreaTagsDto? TagSource);
