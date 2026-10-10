using OpcBridge.Logic;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Logic app's page contract: the editor ids and functions the SPA carries, the Blocks
/// dialog that manages the list, the guide surfaces and the live-chip refresh rule. These
/// are the assertions the bridge dashboard's tests made for its Logic tab before the
/// feature moved into its own app.
/// </summary>
public sealed class LogicAppPageTests
{
    [Fact]
    public void Html_ShipsTheEditorAndDialogs()
    {
        Assert.Contains("id=\"view-logic\"", LogicAppPage.Html, StringComparison.Ordinal);
        foreach (string id in new[]
        {
            "logicEditor", "logicEditorTitle", "logicLiveBadge", "logicMessage", "logicEditorMsg",
            "logicStateHint", "btnLogicSave",
            "lgName", "lgGroup", "lgKind", "lgEnabled", "lgDescription", "lgTags",
            "logicConditions", "btnLogicAddCondition", "btnLogicToNetwork",
            "logicSteps", "btnLogicAddStep", "logicActions", "btnLogicAddAction",
            "logicElements", "btnLogicAddElement", "btnLogicToConditions",
            "logicBlocksDialog", "logicBlockList", "logicCount", "btnLogicAdd", "btnLogicBlocksClose",
            "guideDialog", "guideToc", "guidePane", "notesBody", "btnGuide", "btnGuideTab", "btnNotesTab",
            "bridgeDot", "bridgeAddress", "bridgeStateBadge", "appVersion",
        })
        {
            Assert.Contains($"id=\"{id}\"", LogicAppPage.Html);
        }

        // The cramped split screen is gone: the editor spans the window and the block list
        // lives in the Blocks dialog.
        Assert.DoesNotContain("logic-layout", LogicAppPage.Html, StringComparison.Ordinal);
        int dialog = LogicAppPage.Html.IndexOf("id=\"logicBlocksDialog\"", StringComparison.Ordinal);
        int list = LogicAppPage.Html.IndexOf("id=\"logicBlockList\"", StringComparison.Ordinal);
        Assert.True(dialog >= 0 && list > dialog, "the block list must live inside the dialog");

        // The theme switch ships its own styling — the markup alone left the icon SVGs at
        // their natural size and the header unusable (caught in the browser pass).
        Assert.Contains(".theme-opt {", LogicAppPage.Html, StringComparison.Ordinal);
        Assert.Contains(".theme-opt:has(input:checked)", LogicAppPage.Html, StringComparison.Ordinal);
        Assert.Contains(".theme-ico {", LogicAppPage.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_AuthorsBlocksAndManagesTheListFromTheDialog()
    {
        foreach (string name in new[]
        {
            "loadTags", "loadLogic", "loadLogicState", "renderLogicView", "renderLogicLive",
            "collectLogicDraft", "saveLogicBlock", "deleteLogicBlock", "logicConditionRow",
            "logicStepCard", "logicActionRow",
        })
        {
            Assert.Contains($"function {name}(", LogicAppPage.Script, StringComparison.Ordinal);
        }

        // The tags the phone shows as chips are read from and written to the editor's box.
        Assert.Contains("el('lgTags')", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("block.tags =", LogicAppPage.Script, StringComparison.Ordinal);
        // The interlock group (the phone's heading) and the gate fields: an OR group label and
        // a hold timer per condition, both read back from the row.
        Assert.Contains("el('lgGroup')", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("block.group =", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-field=\"group\"", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-field=\"hold\"", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("holdMs: Number.isFinite(holdSeconds) && holdSeconds > 0 ? Math.round(holdSeconds * 1000) : 0", LogicAppPage.Script, StringComparison.Ordinal);
        // The block list carries the same headings the phone shows.
        Assert.Contains("class=\"logic-group-head\"", LogicAppPage.Script, StringComparison.Ordinal);

        // The IEC 61131-3 network editor: rows nest by indent, the kind drives the fields.
        Assert.Contains("function logicElementRow(", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("function logicElementTreeFromDom(", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("function logicExpandConditions(", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("function logicRenderNetwork(", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-action=\"logic-element-indent\"", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-action=\"logic-element-outdent\"", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-action=\"logic-element-remove\"", LogicAppPage.Script, StringComparison.Ordinal);
        foreach (string kind in new[] { "and", "or", "xor", "not", "ton", "tof", "tp", "ctu", "ctd", "sr", "rs", "r_trig", "f_trig" })
        {
            Assert.Contains($"['{kind}',", LogicAppPage.Script, StringComparison.Ordinal);
        }

        Assert.Contains("/api/logic/blocks", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("/api/logic/state", LogicAppPage.Script, StringComparison.Ordinal);
        // The guide and the release notes render through the markdown helpers — inlineFmt is
        // what renderMarkdown calls for bold/code/links, and the guide failed live without it.
        Assert.Contains("const inlineFmt =", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("renderMarkdown(reflowWrappedLines(", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-action=\"logic-select\"", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-action=\"logic-remove-condition\"", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-action=\"logic-step-up\"", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-action=\"logic-step-down\"", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-action=\"logic-remove-action\"", LogicAppPage.Script, StringComparison.Ordinal);

        // The Blocks dialog picks a block and manages the list: add, per-block delete, and
        // selecting closes the dialog so the editor gets the window.
        Assert.Contains("function openLogicBlocks(", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("function closeLogicBlocks(", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-action=\"logic-delete-block\"", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("closeLogicBlocks();", FunctionBody(LogicAppPage.Script, "function selectLogicBlock("), StringComparison.Ordinal);
        Assert.DoesNotContain("btnLogicDelete", LogicAppPage.Script, StringComparison.Ordinal);

        // The pickers read the trusted tag snapshot; the dashboard-only endpoints are gone.
        Assert.Contains("function logicSourceList(", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("/api/hmi/tags", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("function logicTagOptions(", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("get(tag, 'writeable') === true", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("state.mappings", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("state.sources", LogicAppPage.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_RefreshRepaintsOnlyTheLiveChips()
    {
        // The 1 s poll re-fetches the evaluated state and repaints only the chips — the
        // editor inputs are never rebuilt while someone is typing.
        Assert.Contains("state.logicStateById = {}", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("setInterval(() => { if (!document.hidden) loadLogicState(); }, 1000);", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-logic-badge", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-logic-reason", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-logic-condition-live", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("data-logic-step-live", LogicAppPage.Script, StringComparison.Ordinal);
        string renderLive = FunctionBody(LogicAppPage.Script, "function renderLogicLive(");
        Assert.DoesNotContain(".value =", renderLive, StringComparison.Ordinal);
        // A boolean condition reads as 1 or 0; a numeric one keeps its value in the chip.
        Assert.Contains("const isBoolean = op === 'on' || op === 'off';", renderLive, StringComparison.Ordinal);
        Assert.Contains("s === 'true' ? '1' : s === 'false' ? '0' : 'no data'", renderLive, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_KeepsTheLastKnownBlocksWhenTheBridgeIsUnreachable()
    {
        // Definitions, state and tags cache in the browser; an unreachable bridge shows the
        // last known blocks, flags the connection and disables editing.
        Assert.Contains("opcbridge.logic.defs", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("opcbridge.logic.state", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("opcbridge.logic.tags", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("function cacheRead(", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("function setBridgeOnline(", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("save.disabled = !ok;", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("if (state.logicOffline) throw new Error('The bridge is unreachable — editing is disabled.');", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("unreachable", LogicAppPage.Script, StringComparison.Ordinal);
        Assert.Contains("/api/bridge/status", LogicAppPage.Script, StringComparison.Ordinal);
    }

    private static string FunctionBody(string script, string signature)
    {
        int start = script.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found in the script");
        int end = script.IndexOf("\nfunction ", start + signature.Length, StringComparison.Ordinal);
        return end < 0 ? script[start..] : script[start..end];
    }
}
