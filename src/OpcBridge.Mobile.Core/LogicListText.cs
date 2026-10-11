namespace OpcBridge.Mobile.Core;

/// <summary>
/// The Logic list's empty-state wording. Each message names the one thing that is true right
/// now — no bridge picked, a filter that matched nothing, a bridge that answered with no
/// blocks, or a bridge that was never reached. An empty list must not read like an empty
/// plant when nothing has actually synced.
/// </summary>
public static class LogicListText
{
    /// <summary>No saved bridge is selected.</summary>
    public const string NoBridgeText = "No bridge selected — add one under Settings ▸ Bridges.";

    /// <summary>The bridge answered and has no blocks authored.</summary>
    public const string NoBlocksText =
        "No logic blocks on this bridge yet — author them in the OpcBridge HMI under View ▸ Logic.";

    /// <summary>The bridge never answered because it wants a session first.</summary>
    public const string SignInText =
        "No data yet — the bridge requires sign-in. Sign in under Settings to sync the blocks.";

    /// <summary>
    /// What the empty list means for the current bridge and link: the filter-miss text when
    /// cards exist but the search or a filter hid them, otherwise the honest reason there is
    /// nothing to show — including "nothing has synced yet" when the bridge was never reached.
    /// </summary>
    public static string EmptyList(
        bool hasBridge,
        bool connected,
        bool hasBlocks,
        bool signInRequired,
        string bridgeStatus,
        string filterMissText)
    {
        if (!hasBridge)
        {
            return NoBridgeText;
        }

        if (hasBlocks)
        {
            return filterMissText;
        }

        if (connected)
        {
            return NoBlocksText;
        }

        return signInRequired
            ? SignInText
            : "No data yet — " + bridgeStatus + ". Connect once to sync the blocks.";
    }
}
