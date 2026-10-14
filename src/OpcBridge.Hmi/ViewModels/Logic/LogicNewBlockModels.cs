namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// What the New-block dialog is offered: the interlock groups and areas already in use, for its
/// suggestion lists, plus the values to pre-fill so sibling blocks are created without retyping.
/// </summary>
public sealed record LogicNewBlockRequest(
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Areas,
    string Group,
    string Area);

/// <summary>
/// What the New-block dialog returned: the new block's name, interlock group and area. The area
/// is assigned here, first, because it is the scope every tag picked afterwards is shared by.
/// </summary>
public sealed record LogicNewBlockDraft(string Name, string Group, string Area);
