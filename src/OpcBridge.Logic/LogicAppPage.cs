namespace OpcBridge.Logic;

/// <summary>
/// The app's page: the Logic editor, the Result view and their live state, served at "/"
/// as one document. The HTML and script below are a test-asserted DOM/JS contract —
/// tests/OpcBridge.LoadTest/LogicAppPageTests.cs fails when an id or function the contract
/// names is renamed:
///
///   Tabs: "viewTabs" (role="tablist") with "tabLogicEditor" (view id="view-logic") and
///   "tabLogicResult" (view id="view-result": "logicResultSummary", "logicResultList",
///   "logicResultDetail").
///   Editor: id="view-logic", "logicEditor", "logicEditorTitle", "logicLiveBadge",
///   "logicMessage", "logicEditorMsg", "logicStateHint", "btnLogicSave",
///   "lgName"/"lgKind"/"lgEnabled"/"lgDescription"/"lgGroup"/"lgTags",
///   "logicConditions"/"btnLogicAddCondition"/"btnLogicToNetwork",
///   "logicElements"/"btnLogicAddElement"/"btnLogicToConditions",
///   "logicSteps"/"btnLogicAddStep"/"logicActions"/"btnLogicAddAction",
///   data-action="logic-element-indent"/"logic-element-outdent"/"logic-element-remove",
///   "logic-remove-condition"/"logic-add-step-condition"/"logic-step-up"/"logic-step-down"/
///   "logic-step-remove"/"logic-remove-action".
///   Blocks dialog: "logicBlocksDialog", "logicBlockList", "logicCount", "btnLogicAdd",
///   "btnLogicBlocksClose", data-action="logic-select"/"logic-delete-block".
///   Guide dialog: "guideDialog", "guideToc", "guidePane", "notesBody", "btnGuide",
///   "btnGuideTab"/"btnNotesTab".
///   Functions: loadTags(/loadLogic(/loadLogicState(/renderLogicView(/renderLogicLive(/
///   renderLogicResult(/showView(/onViewTabKey(/collectLogicDraft(/saveLogicBlock(/
///   deleteLogicBlock(/logicConditionRow(/logicStepCard(/logicActionRow(/logicElementRow(/
///   logicElementTreeFromDom(/logicExpandConditions(/logicRenderNetwork(/logicResultBlockRow(/
///   logicResultElementRow(/logicResultStepCards(.
///   Endpoints: /api/logic, /api/logic/blocks, /api/logic/state, /api/hmi/tags, /api/help,
///   /api/changelog, /api/bridge/status.
/// </summary>
internal static class LogicAppPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>OpcBridge Logic</title>
<style>

        /* ---- OpcBridge Logic shell: the editor owns the window, the block list lives in
           the Blocks dialog, and the guide/release notes share one dialog. ---- */
        .app-head { display: flex; align-items: stretch; gap: 0; min-height: 53px; padding: 0 14px; background: var(--panel); border-bottom: 2px solid var(--text); flex-wrap: wrap; }
        .app-head .pills { margin-left: auto; }
        .app-actions { display: flex; align-items: center; gap: 8px; padding-left: 14px; border-left: 1px solid var(--border); flex-wrap: wrap; }
        .theme-switch { display: flex; align-items: center; border-left: 1px solid var(--border); padding-left: 14px; }
        .app-head .theme-switch { border-left: none; padding-left: 0; }
        .theme-opt { position: relative; display: inline-flex; align-items: center; gap: 6px; padding: 5px 9px; border: 1px solid transparent; color: var(--muted); font-family: var(--font-mono); font-size: var(--fs-micro); font-weight: 600; text-transform: uppercase; letter-spacing: .07em; cursor: pointer; }
        .theme-opt + .theme-opt { margin-left: 2px; }
        .theme-opt:hover { color: var(--text); background: var(--panel2); }
        .theme-opt input { position: absolute; width: 1px; height: 1px; opacity: 0; pointer-events: none; }
        .theme-opt:has(input:checked) { color: var(--panel); background: var(--text); border-color: var(--text); }
        .theme-opt:has(input:focus-visible) { outline: 2px solid var(--focus); outline-offset: 1px; }
        .theme-ico { width: 12px; height: 12px; flex: none; stroke: currentColor; fill: none; stroke-width: 1.7; stroke-linecap: round; stroke-linejoin: round; }
        .logic-dialog { background: var(--panel); color: var(--text); border: 1px solid var(--text); border-top: 3px solid var(--text); border-radius: 0; width: min(760px, 94vw); max-height: 88vh; padding: 0; }
        .logic-dialog::backdrop { background: var(--overlay); }
        .logic-dialog .modal-b { overflow-y: auto; max-height: calc(88vh - 70px); }
        .logic-dialog .list { max-height: none; overflow-y: visible; }
        .logic-block-item { display: flex; align-items: stretch; gap: 6px; }
        .logic-block-item + .logic-block-item { margin-top: 6px; }
        .logic-block-item .logic-block-row { flex: 1; }
        .logic-block-del { flex: none; }
        .guide-dialog { width: min(900px, 94vw); }
        .guide-tabs { display: flex; align-items: center; gap: 6px; margin-left: auto; }
        .guide-toc { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 12px; }
        .guide-toc button { background: none; border: 1px solid var(--border2); color: var(--muted); padding: 4px 9px; font-size: var(--fs-micro); font-family: var(--font-mono); text-transform: uppercase; letter-spacing: .06em; cursor: pointer; }
        .guide-toc button:hover { color: var(--text); background: var(--panel2); }
        .guide-toc button.active { color: var(--panel); background: var(--text); border-color: var(--text); }
        .guide-toc button:focus-visible { outline: 2px solid var(--focus); outline-offset: 1px; }
        .guide-article { display: none; }
        .guide-article.active { display: block; }
        .help-article-title { font-family: var(--font-mono); font-size: var(--fs-title); font-weight: 700; text-transform: uppercase; letter-spacing: .05em; margin: 0 0 10px; }
        .help-body p { margin: 7px 0; }
        .help-body h2 { font-family: var(--font-mono); font-size: var(--fs-title); text-transform: uppercase; letter-spacing: .05em; margin: 16px 0 8px; }
        .help-body h3 { font-size: var(--fs-body); font-weight: 700; margin: 14px 0 6px; }
        .help-body h4, .help-body h5 { font-size: var(--fs-body); font-weight: 700; margin: 12px 0 5px; color: var(--muted-strong); }
        .help-body ul, .help-body ol { margin: 7px 0 7px 20px; }
        .help-body li { margin: 4px 0; }
        .help-body code { font-family: var(--font-mono); font-size: var(--fs-micro); background: var(--panel2); border: 1px solid var(--border); padding: 0 4px; }
        .help-body pre { background: var(--panel2); border: 1px solid var(--border); padding: 10px; overflow-x: auto; margin: 10px 0; }
        .help-body pre code { background: none; border: none; padding: 0; font-size: var(--fs-micro); }
        .help-body hr { border: none; border-top: 1px solid var(--border2); margin: 16px 0; }
        .help-table-wrap { overflow-x: auto; margin: 10px 0; }
        .help-body table { border-collapse: collapse; font-variant-numeric: tabular-nums; }
        .help-body th, .help-body td { border: 1px solid var(--border2); padding: 5px 9px; font-size: var(--fs-body); text-align: left; }
        @media (max-width: 700px) {
            .app-head { padding: 0 10px; }
            .view { padding: 14px 12px 48px; }
            .logic-dialog { width: 100%; max-height: 100vh; border-left: none; border-right: none; }
        }
        @media (pointer: coarse) {
            .btn { min-height: 44px; min-width: 44px; padding: 0 14px; }
            .view-tab { min-height: 44px; padding: 0 14px; }
            input[type=checkbox] { min-width: 24px; min-height: 24px; }
            select, input[type=text], input[type=number] { min-height: 44px; }
            .modal-close { min-width: 44px; min-height: 44px; }
            .info { position: relative; }
            .info::after { content: ''; position: absolute; inset: -7px; }
        }

        :root {
            color-scheme: light;
            --paper: #f1f2ef;
            --bg: #f1f2ef;
            --panel: #ffffff;
            --panel2: #f6f7f4;
            --border: #e2e4df;
            --border2: #c6cac3;
            --text: #14181a;
            --ink2: #3b4544;
            --muted: #55605f;
            --muted-strong: #39443f;
            --good: #0f6b3d;
            --bad: #a52118;
            --warn: #8a5a00;
            --info: #1f4e79;
            --accent: #14181a;
            --focus: #1d4ed8;
            /* Status grounds: tinted surfaces for badges and banners. State text
               always pairs with its own ground, never with the page surface. */
            --good-bg: #eaf2ec; --good-border: #bcd3c4; --good-text: #0b4d2c;
            --warn-bg: #f8f1e2; --warn-border: #ddc9a0; --warn-text: #6b4500;
            --bad-bg: #f7e9e8; --bad-border: #e0b6b2; --bad-text: #8a1c14;
            --info-bg: #ebf0f6; --info-border: #c3d0de;
            --fed-bg: #e8f0fe; --fed-text: #1859b8;
            --overlay: rgba(20,24,26,.45);
            --btn-hover: #000;
            --font-ui: system-ui, -apple-system, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
            --font-mono: ui-monospace, 'SF Mono', 'Cascadia Mono', 'Segoe UI Mono', Consolas, 'Liberation Mono', monospace;
            /* Five steps, each with one job. Nothing else in this sheet sets a
               font-size. micro: labels, stamps, table heads, timestamps, metadata.
               body: UI text, controls, table data, buttons, hints. title: headings,
               names, readings. value: primary readings and the state stamps.
               read: the single hero figure in a focused dialog. */
            --fs-micro: 11px;
            --fs-body: 13px;
            --fs-title: 16px;
            --fs-value: 20px;
            --fs-read: 26px;
            font-family: var(--font-ui);
        }
        :root[data-theme="dark"] {
            color-scheme: dark;
            --paper: #0f1312;
            --bg: #0f1312;
            --panel: #171c1a;
            --panel2: #1f2523;
            --border: #262d2b;
            --border2: #3b4441;
            --text: #e8ebe7;
            --ink2: #c2c9c4;
            --muted: #99a49f;
            --muted-strong: #b3bcb7;
            --good: #55c98a;
            --bad: #f0736a;
            --warn: #d9a441;
            --info: #6fb0e0;
            --accent: #e8ebe7;
            --focus: #7aa2ff;
            --good-bg: #16291e; --good-border: #2c5138; --good-text: #8ed7ac;
            --warn-bg: #2a2114; --warn-border: #5c4a24; --warn-text: #e3b563;
            --bad-bg: #2b1917; --bad-border: #5e322d; --bad-text: #f2938a;
            --info-bg: #16232e; --info-border: #2f4a5e;
            --fed-bg: #16233a; --fed-text: #8ab4f8;
            --overlay: rgba(0,0,0,.62);
            --btn-hover: #fff;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { background: var(--bg); color: var(--text); font-size: var(--fs-body); line-height: 1.5; display: flex; flex-direction: column; height: 100vh; height: 100dvh; overflow: hidden; }
        .mono { font-family: var(--font-mono); font-variant-numeric: tabular-nums; }
        .brand { display: flex; align-items: center; gap: 9px; font-weight: 700; font-size: var(--fs-title); letter-spacing: -.01em; white-space: nowrap; padding-right: 14px; }
        .dot { width: 9px; height: 9px; border-radius: 1px; background: var(--good); flex: none; }
        .ver { font-family: var(--font-mono); font-size: var(--fs-micro); font-weight: 400; color: var(--muted); border: 1px solid var(--border2); border-radius: 2px; padding: 0 5px; margin-left: 2px; }
        .dot.off { background: var(--bad); }
        .pills { display: flex; align-items: stretch; gap: 0; margin-left: 0; flex-wrap: wrap; }
        .pill { display: flex; align-items: center; gap: 7px; background: none; border: none; border-left: 1px solid var(--border); border-radius: 0; padding: 6px 13px; font-size: var(--fs-body); white-space: nowrap; }
        .pill b { font-family: var(--font-mono); font-variant-numeric: tabular-nums; font-weight: 600; font-size: var(--fs-body); }
        .pill .k { color: var(--muted); text-transform: uppercase; font-size: var(--fs-micro); letter-spacing: .08em; font-family: var(--font-mono); }
        .pill .badge, .pill .good, .pill .bad, .pill .warn { font-size: var(--fs-micro); }
        .pill.fed { background: var(--fed-bg); color: var(--fed-text); }
.content { flex: 1; min-width: 0; overflow: auto; background: var(--paper); }
.view { display: none; padding: 16px 20px 56px; max-width: 1600px; margin-inline: auto; }
.view.active { display: block; }
        .skip-link { position: absolute; inset-inline-start: -9999px; top: 8px; z-index: 2000; background: var(--panel); color: var(--text); border: 2px solid var(--text); padding: 8px 14px; font-size: var(--fs-body); font-weight: 700; font-family: var(--font-mono); text-transform: uppercase; letter-spacing: .08em; }
        .skip-link:focus { inset-inline-start: 8px; }
        /* Announced but not shown: state that changes without moving focus (search result counts). */
        .sr-only { position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; border: 0; }
        /* The view heading is structure first: it names the page for assistive tech and
           tells anyone arriving from the rail where they just landed. */
        .view-title { font-family: var(--font-mono); font-size: var(--fs-title); font-weight: 700; text-transform: uppercase; letter-spacing: .06em; color: var(--text); padding-bottom: 8px; border-bottom: 1px solid var(--border2); margin-bottom: 14px; }
        .view-title:focus { outline: none; }
        .view-title:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
        .box { background: var(--panel); border: 1px solid var(--border); border-top: 2px solid var(--text); border-radius: 0; overflow: hidden; }
        .box-h { padding: 8px 12px; background: none; border-bottom: 1px solid var(--border2); font-size: var(--fs-micro); font-weight: 700; text-transform: uppercase; letter-spacing: .1em; color: var(--muted-strong); display: flex; align-items: center; gap: 8px; font-family: var(--font-mono); }
        .box-b { padding: 12px; }
        .badge { display: inline-flex; align-items: center; gap: 5px; padding: 0 6px; border-radius: 2px; font-size: var(--fs-micro); font-weight: 700; font-family: var(--font-mono); font-variant-numeric: tabular-nums; text-transform: uppercase; letter-spacing: .04em; border: 1px solid currentColor; }
        .badge::before { content:''; width:5px; height:5px; border-radius:0; background:currentColor; }
        .badge.good { color: var(--good); background: var(--good-bg); }
        .badge.bad { color: var(--bad); background: var(--bad-bg); }
        .badge.warn { color: var(--warn); background: var(--warn-bg); }
        .badge.partial { color: var(--info); background: var(--info-bg); }
        .field { display: flex; align-items: center; gap: 8px; margin-bottom: 10px; flex-wrap: wrap; }
        .field:last-child { margin-bottom: 0; }
        label.fl { color: var(--muted); font-size: var(--fs-micro); font-weight: 600; text-transform: uppercase; letter-spacing: .08em; width: 104px; flex-shrink: 0; font-family: var(--font-mono); }
        .field > label.fl { display: flex; align-items: center; gap: 7px; }
        select, input[type=text], input[type=password] { background: var(--panel); color: var(--text); border: 1px solid var(--border2); border-radius: 2px; padding: 5px 8px; font-size: var(--fs-body); font-family: var(--font-ui); }
        input[type=number] { background: var(--panel); color: var(--text); border: 1px solid var(--border2); border-radius: 2px; padding: 5px 8px; font-size: var(--fs-body); font-family: var(--font-mono); font-variant-numeric: tabular-nums; }
        input[type=text], input[type=password], select { min-width: 140px; }
        input:disabled, select:disabled { background: var(--panel2); color: var(--muted); cursor: not-allowed; }
        select:focus-visible, input:focus-visible, textarea:focus-visible { outline: 2px solid var(--focus); outline-offset: 1px; border-color: var(--text); }
        .btn { display: inline-flex; align-items: center; gap: 6px; background: var(--text); color: var(--panel); border: 1px solid var(--text); border-radius: 2px; padding: 5px 12px; font-size: var(--fs-body); font-weight: 600; cursor: pointer; white-space: nowrap; font-family: var(--font-ui); }
        .btn:hover { background: var(--btn-hover); }
        .btn.ghost { background: transparent; color: var(--text); border: 1px solid var(--border2); }
        .btn.ghost:hover { background: var(--panel2); border-color: var(--text); }
        .btn:disabled, .btn[disabled] { opacity: .45; cursor: not-allowed; }
        .btn:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
        .hint, .msg { font-size: var(--fs-body); color: var(--muted); }
        .list { display: flex; flex-direction: column; gap: 0; max-height: 380px; overflow-y: auto; }
        .tag-browser-toolbar { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 8px; align-items: center; }
        .tag-browser-toolbar .msg { flex: 1; }
        .modal-overlay { display: none; position: fixed; inset: 0; background: var(--overlay); z-index: 1000; justify-content: center; align-items: center; padding: 16px; }
        .modal-overlay.open { display: flex; }
        .modal { background: var(--panel); border: 1px solid var(--text); border-top: 3px solid var(--text); border-radius: 0; width: min(560px, 92vw); max-height: 90vh; overflow-y: auto; box-shadow: none; }
        .modal-h { display: flex; align-items: start; justify-content: space-between; gap: 12px; padding: 13px 16px; border-bottom: 1px solid var(--border2); }
        .modal-h .n { font-size: var(--fs-title); font-weight: 700; letter-spacing: -.01em; }
        .modal-h .p { font-size: var(--fs-micro); color: var(--muted); font-family: var(--font-mono); margin-top: 4px; }
        .modal-close { background: none; border: 1px solid transparent; color: var(--muted); font-size: var(--fs-value); cursor: pointer; padding: 0 6px; line-height: 1.3; border-radius: 2px; font-family: var(--font-mono); }
        .modal-close:hover { color: var(--text); border-color: var(--border2); }
        .modal-close:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
        .modal-b { padding: 16px; display: flex; flex-direction: column; gap: 14px; }
        .fp-panel { background: var(--panel2); border: 1px solid var(--border2); border-radius: 0; padding: 12px; }
        .fp-k { color: var(--muted); font-size: var(--fs-micro); font-weight: 700; text-transform: uppercase; letter-spacing: .09em; margin-bottom: 7px; font-family: var(--font-mono); }
        .info { display: inline-flex; align-items: center; justify-content: center; width: 15px; height: 15px; border-radius: 50%; background: var(--panel); border: 1px solid var(--border2); color: var(--muted); font-size: var(--fs-micro); font-weight: 700; font-style: italic; cursor: help; margin-left: 3px; user-select: none; vertical-align: middle; font-family: var(--font-ui); }
        .info:hover { color: var(--panel); background: var(--text); border-color: var(--text); }
        .tip { position: fixed; z-index: 9999; background: var(--panel); color: var(--text); border: 1px solid var(--text); border-radius: 0; padding: 8px 11px; font-size: var(--fs-body); font-weight: 400; line-height: 1.55; max-width: 300px; box-shadow: none; pointer-events: none; opacity: 0; transition: opacity .1s ease; }
        .tip.show { opacity: 1; }
        /* One system-wide answer to reduced motion: nothing in this surface animates. */
        @media (prefers-reduced-motion: reduce) {
            *, *::before, *::after {
                animation-duration: .001ms !important;
                animation-iteration-count: 1 !important;
                transition-duration: .001ms !important;
                scroll-behavior: auto !important;
            }
        }
        /* iOS Safari zooms the viewport on focus below 16px, which shifts the layout
           sideways mid-entry. Same selectors as the base rule so the size actually wins. */
        @media (max-width: 640px) {
            select, input[type=text], input[type=password], input[type=number], input[type=search], textarea { font-size: 16px; }
        }
        /* Conditions and steps render as compact rows carrying a live state chip per
           condition; the block list lives in the Blocks dialog. */
        .logic-block-row { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; width: 100%; text-align: left; padding: 6px 8px; border: 1px solid var(--border2); background: var(--panel); color: var(--text); cursor: pointer; }
        .logic-block-row + .logic-block-row { margin-top: 6px; }
        .logic-block-row.active { border-color: var(--accent); background: var(--panel2); }
        .logic-block-row .logic-row-name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .logic-block-row .logic-row-reason { flex: 1 0 100%; min-width: 0; color: var(--muted); font-size: var(--fs-micro); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .logic-cond-row, .logic-action-row { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; padding: 6px 8px; border: 1px solid var(--border2); background: var(--panel); }
        .logic-cond-row + .logic-cond-row, .logic-action-row + .logic-action-row { margin-top: 6px; }
        .logic-cond-row input[data-field="text"] { flex: 1 1 220px; min-width: 160px; }
        .logic-cond-row select, .logic-action-row select { max-width: 190px; }
        .logic-cond-row input[data-field="value"] { width: 90px; }
        .logic-cond-row input[data-field="nextStep"] { flex: 1 1 180px; min-width: 140px; }
        .logic-live { font-family: var(--font-mono); font-size: var(--fs-micro); padding: 1px 6px; border: 1px solid var(--border2); white-space: nowrap; }
        .logic-live.true { color: var(--good); border-color: var(--good); }
        .logic-live.false { color: var(--bad); border-color: var(--bad); }
        .logic-live.unknown, .logic-live.disabled { color: var(--muted); }
        .logic-step { border: 1px solid var(--border2); background: var(--panel2); padding: 8px; }
        .logic-step + .logic-step { margin-top: 8px; }
        .logic-step.current { border-color: var(--accent); }
        .logic-step-head { display: flex; gap: 6px; align-items: center; margin-bottom: 6px; flex-wrap: wrap; }
        .logic-step-head input[data-field="stepName"] { flex: 1 1 160px; min-width: 120px; }
        .logic-step-conditions { margin-top: 6px; }
        .logic-reason { color: var(--warn); font-size: var(--fs-micro); }
        /* One network element row: the indent is set inline, the kind drives which fields show. */
        .logic-element-row { align-items: flex-start; }
        .logic-element-row select[data-field="kind"] { max-width: 190px; }
        .logic-element-row .logic-element-label { flex: 1 1 200px; min-width: 150px; }
        /* The block list groups blocks by their interlock group — the same heading the phone shows. */
        .logic-group-head { margin: 8px 0 4px; font-size: var(--fs-micro); letter-spacing: .08em; text-transform: uppercase; color: var(--muted); }
        /* ---- Editor / Result tabs ------------------------------------------------------
           The strip switches the two views. The Result view presents the evaluated
           configuration the phone shows: the block list left, the selected block's network
           and steps right, read-only, refreshed by the same 1 s state poll. */
        .view-tabs { display: flex; align-items: stretch; padding: 0 14px; background: var(--panel); border-bottom: 1px solid var(--border2); }
        .view-tab { background: none; border: none; border-bottom: 2px solid transparent; padding: 9px 12px; font-family: var(--font-mono); font-size: var(--fs-micro); font-weight: 600; text-transform: uppercase; letter-spacing: .08em; color: var(--muted); cursor: pointer; }
        .view-tab + .view-tab { margin-left: 2px; }
        .view-tab:hover { color: var(--text); background: var(--panel2); }
        .view-tab.active { color: var(--text); border-bottom-color: var(--text); font-weight: 700; }
        .view-tab:focus-visible { outline: 2px solid var(--focus); outline-offset: -2px; }
        .result-grid { display: grid; grid-template-columns: minmax(240px, 340px) minmax(0, 1fr); gap: 14px; align-items: start; }
        .result-list { display: flex; flex-direction: column; }
        .result-head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-bottom: 8px; }
        .result-title { font-size: var(--fs-title); font-weight: 700; letter-spacing: -.01em; }
        .result-reason { margin-bottom: 10px; }
        .result-reason.bad { color: var(--bad); }
        .result-reason.good { color: var(--muted); }
        .result-el { display: flex; flex-wrap: wrap; align-items: baseline; gap: 8px; padding: 6px 8px; border: 1px solid var(--border); background: var(--panel2); }
        .result-el + .result-el { margin-top: 6px; }
        .result-mark { font-family: var(--font-mono); font-weight: 700; width: 13px; text-align: center; }
        .result-mark.true { color: var(--good); }
        .result-mark.false { color: var(--bad); }
        .result-mark.unknown { color: var(--muted); }
        .result-kind { font-family: var(--font-mono); font-size: var(--fs-micro); color: var(--muted); min-width: 46px; text-transform: uppercase; }
        .result-text { flex: 1 1 200px; min-width: 150px; }
        .result-warn { font-size: var(--fs-micro); color: var(--warn); }
        .result-actual { font-family: var(--font-mono); font-size: var(--fs-value); font-weight: 700; line-height: 1.1; margin-left: auto; }
        .result-actual.true { color: var(--good); }
        .result-actual.false { color: var(--bad); }
        .result-actual.unknown { color: var(--muted); }
        .result-value { color: var(--muted); font-size: var(--fs-micro); font-family: var(--font-mono); }
        .result-should, .result-progress, .result-tag, .result-next { flex: 1 0 100%; font-size: var(--fs-micro); }
        .result-should { font-family: var(--font-mono); }
        .result-should.true { color: var(--good); }
        .result-should.false { color: var(--bad); }
        .result-should.unknown { color: var(--muted); }
        .result-progress { color: var(--warn); font-family: var(--font-mono); }
        .result-next { color: var(--warn); }
        .result-tag { color: var(--muted); }
        .result-step-name { flex: 1; min-width: 120px; font-weight: 700; }
        .result-step-state { font-family: var(--font-mono); font-size: var(--fs-micro); font-weight: 700; text-transform: uppercase; letter-spacing: .06em; color: var(--muted); }
        .result-step-state.done { color: var(--good); }
        .result-step-state.current { color: var(--warn); }
        .result-step-state.pending { color: var(--muted); }
        .result-step-state.unknown { color: var(--muted); }
        @media (max-width: 900px) { .result-grid { grid-template-columns: 1fr; } }
</style>
</head>
<body>
    <header class="app-head">
        <span class="brand"><span class="dot" id="bridgeDot"></span>OpcBridge Logic<span class="ver" id="appVersion"></span></span>
        <span class="pills">
            <span class="pill"><span class="k">Bridge</span><b id="bridgeAddress">…</b><span class="badge" id="bridgeStateBadge">checking</span></span>
        </span>
        <span class="app-actions">
            <button class="btn" type="button" id="btnLogicBlocks">Blocks</button>
            <button class="btn ghost" type="button" id="btnGuide">Guide</button>
            <span class="theme-switch" role="group" aria-label="Theme">
                <label class="theme-opt" title="Light"><input type="radio" name="theme" value="light"><svg class="theme-ico" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="4"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M19.1 4.9L17 7M7 17l-2.1 2.1"/></svg>Light</label>
                <label class="theme-opt" title="Dark"><input type="radio" name="theme" value="dark"><svg class="theme-ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z"/></svg>Dark</label>
                <label class="theme-opt" title="Follow the system"><input type="radio" name="theme" value="system"><svg class="theme-ico" viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="4" width="18" height="12" rx="1"/><path d="M8 20h8M12 16v4"/></svg>System</label>
            </span>
        </span>
    </header>
    <nav class="view-tabs" id="viewTabs" role="tablist" aria-label="Logic views">
        <button class="view-tab active" type="button" id="tabLogicEditor" role="tab" aria-selected="true" aria-controls="view-logic" tabindex="0" data-view="editor">Editor</button>
        <button class="view-tab" type="button" id="tabLogicResult" role="tab" aria-selected="false" aria-controls="view-result" tabindex="-1" data-view="result">Result</button>
    </nav>
    <main class="content">
        <div class="view active" id="view-logic">
            <h1 class="view-title" tabindex="-1">Editor</h1>
            <div class="box">
                <div class="box-h">Plant logic</div>
                <div class="box-b">
                    <div class="hint" id="logicMessage" role="status" style="margin-bottom:10px">Author the plant logic the <b>OpcBridge Logic</b> phone app shows: <b>interlock</b> / <b>permissive</b> blocks (every condition and every OR group must be satisfied — the AND) or <b>sequence</b> blocks whose ordered steps are completed one after another. A condition's <b>ON (NO contact)</b> / <b>OFF (NC contact)</b> sets the reading it must show, an <b>OR group</b> makes its members any-of, and a <b>hold</b> makes a contact wait for a steady signal. Press <b>Blocks</b> to pick a block or start one; the phone renders each contact as <i>should 1 / actual 0</i> and follows the block's live state.</div>
                    <div class="fp-panel" id="logicEditor">
                        <div class="fp-k"><span id="logicEditorTitle">Block</span><span id="logicLiveBadge" style="margin-left:8px"></span></div>
                        <div class="field"><label class="fl" for="lgName">Name</label><input type="text" id="lgName" maxlength="64" style="flex:1" placeholder="Line 01 Start"></div>
                        <div class="field"><label class="fl" for="lgKind">Kind <span class="info" data-tip="Interlock / permissive: every block-severity condition must be true. Sequence: ordered steps, each completed when its own conditions are true (and its optional handshake tag is true), the first incomplete step being the current one.">i</span></label><select id="lgKind"><option value="interlock">Interlock (all must be true)</option><option value="permissive">Permissive (all must be true)</option><option value="sequence">Sequence (ordered steps)</option></select></div>
                        <div class="field"><label class="fl" for="lgEnabled">Enabled</label><input type="checkbox" id="lgEnabled" checked></div>
                        <div class="field"><label class="fl" for="lgDescription">Description</label><input type="text" id="lgDescription" style="flex:1" placeholder="Shown on the phone under the block name"></div>
                        <div class="field"><label class="fl" for="lgGroup">Interlock group <span class="info" data-tip="Optional. The phone collects blocks carrying the same group under one collapsible heading (e.g. Primary Arm), so a machine's up/down interlocks read together instead of as a flat list. At most 40 characters.">i</span></label><input type="text" id="lgGroup" maxlength="40" style="flex:1" placeholder="Primary Arm"></div>
                        <div class="field"><label class="fl" for="lgTags">Tags <span class="info" data-tip="Comma-separated labels that group blocks on the phone (e.g. Line 1, Safety). Up to 8, each at most 24 characters; blanks and duplicates are dropped. The phone shows them as chips on the card and can filter the list by them.">i</span></label><input type="text" id="lgTags" style="flex:1" placeholder="Line 1, Safety"></div>
                        <div id="logicConditionsBlock">
                            <div class="fp-k" style="margin-top:10px">Conditions
                                <button class="btn ghost" type="button" id="btnLogicAddCondition" style="float:right;margin-top:-3px">+ Condition</button>
                                <button class="btn ghost" type="button" id="btnLogicToNetwork" style="float:right;margin-top:-3px;margin-right:6px" title="Rewrite these conditions as an IEC network you can nest (AND of ORs, timers, blocks).">Convert to IEC network</button>
                            </div>
                            <div class="list" id="logicConditions"></div>
                        </div>
                        <div id="logicNetworkBlock" style="display:none">
                            <div class="fp-k" style="margin-top:10px">Network (IEC 61131-3)
                                <span class="info" data-tip="The block's logic as a network of contacts, gates and standard function blocks. Rows nest by their indent: a row's inputs are the rows under it at a deeper level, and the outermost rows are the block's roots (all must be true). Contacts are NO / NC (or a comparison); gates are AND / OR / XOR / NOT; blocks are TON / TOF / TP timers, CTU / CTD counters, SR / RS latches and R_TRIG / F_TRIG edge triggers.">i</span>
                                <button class="btn ghost" type="button" id="btnLogicAddElement" style="float:right;margin-top:-3px">+ Element</button>
                                <button class="btn ghost" type="button" id="btnLogicToConditions" style="float:right;margin-top:-3px;margin-right:6px" title="Drop the network and go back to the flat condition list.">Back to conditions</button>
                            </div>
                            <div class="list" id="logicElements"></div>
                            <div class="hint" style="margin-top:4px">Indent with → to make a row an input of the row above it; the phone shows every row with its live state and the reading it needs.</div>
                        </div>
                        <div id="logicStepsBlock" style="display:none">
                            <div class="fp-k" style="margin-top:10px">Steps <button class="btn ghost" type="button" id="btnLogicAddStep" style="float:right;margin-top:-3px">+ Step</button></div>
                            <div class="list" id="logicSteps"></div>
                        </div>
                        <div class="fp-k" style="margin-top:10px">Actions <span class="info" data-tip="Buttons the phone app shows for this block or step. A press writes the value into the tag through the bridge's normal write path — the tag must be writeable.">i</span><button class="btn ghost" type="button" id="btnLogicAddAction" style="float:right;margin-top:-3px">+ Action</button></div>
                        <div class="list" id="logicActions"></div>
                        <div class="tag-browser-toolbar" style="margin-top:10px">
                            <button class="btn" type="button" id="btnLogicSave">Save Block</button>
                            <span class="msg" id="logicEditorMsg" role="status"></span>
                        </div>
                        <div class="hint" id="logicStateHint" style="margin-top:6px">Live state appears here for a saved block.</div>
                    </div>
                </div>
            </div>
        </div>
        <div class="view" id="view-result">
            <h1 class="view-title" tabindex="-1">Result</h1>
            <div class="result-grid">
                <div class="box">
                    <div class="box-h">Blocks <span class="hint" id="logicResultSummary" style="margin-left:auto"></span></div>
                    <div class="box-b"><div class="result-list" id="logicResultList"><span class="msg">Loading…</span></div></div>
                </div>
                <div class="box">
                    <div class="box-h">Evaluated block</div>
                    <div class="box-b" id="logicResultDetail"><span class="msg">Loading…</span></div>
                </div>
            </div>
        </div>
    </main>
    <dialog id="logicBlocksDialog" class="logic-dialog" aria-labelledby="logicBlocksTitle">
        <div class="modal-h">
            <div>
                <div class="n" id="logicBlocksTitle">Logic blocks</div>
                <div class="p" id="logicCount">No blocks</div>
            </div>
            <div class="guide-tabs">
                <button class="btn" type="button" id="btnLogicAdd">+ New block</button>
                <button class="modal-close" type="button" id="btnLogicBlocksClose" aria-label="Close">✕</button>
            </div>
        </div>
        <div class="modal-b">
            <div class="list" id="logicBlockList"><span class="msg">Loading…</span></div>
        </div>
    </dialog>
    <dialog id="guideDialog" class="logic-dialog guide-dialog" aria-labelledby="guideTitle">
        <div class="modal-h">
            <div>
                <div class="n" id="guideTitle">Guide</div>
                <div class="p">Plant logic — authoring and live state</div>
            </div>
            <div class="guide-tabs">
                <button class="btn ghost" type="button" id="btnGuideTab" aria-pressed="true">Guide</button>
                <button class="btn ghost" type="button" id="btnNotesTab" aria-pressed="false">Release notes</button>
                <button class="modal-close" type="button" id="btnGuideClose" aria-label="Close">✕</button>
            </div>
        </div>
        <div class="modal-b">
            <div id="guideWrap">
                <nav class="guide-toc" id="guideToc" aria-label="Guide topics"></nav>
                <div id="guidePane"></div>
            </div>
            <div id="notesWrap" hidden>
                <div class="help-body" id="notesBody"></div>
            </div>
        </div>
    </dialog>
</body>
</html>

""";

    public const string Script = """
<script>
const ESC = {'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'};
const esc = s => String(s ?? '').replace(/[&<>'"]/g, c => ESC[c]);
const attr = esc;
let tipEl;
document.addEventListener('mouseover', e => {
    const info = e.target.closest('.info, [data-tip]');
    if (!info || !info.dataset.tip) return;
    if (!tipEl) { tipEl = document.createElement('div'); tipEl.className = 'tip'; document.body.appendChild(tipEl); }
    tipEl.textContent = info.dataset.tip;
    tipEl.classList.add('show');
    const r = info.getBoundingClientRect();
    const tr = tipEl.getBoundingClientRect();
    let x = r.left + r.width / 2 - tr.width / 2;
    let y = r.top - tr.height - 6;
    if (y < 4) y = r.bottom + 6;
    if (x < 4) x = 4;
    tipEl.style.left = x + 'px';
    tipEl.style.top = y + 'px';
});
document.addEventListener('mouseout', e => { if (e.target.closest('.info, [data-tip]') && tipEl) tipEl.classList.remove('show'); });
const el = id => document.getElementById(id);
const THEME_KEY = 'opcbridge.theme';
const THEME_PREFS = ['light', 'dark', 'system'];
const systemTheme = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
let themePaletteCache = null;
let themePref = (() => {
    try {
        const stored = localStorage.getItem(THEME_KEY);
        if (THEME_PREFS.indexOf(stored) >= 0) return stored;
    } catch (e) { }
    return 'system';
})();

function resolvedTheme(pref) {
    if (pref !== 'system') return pref;
    return systemTheme && systemTheme.matches ? 'dark' : 'light';
}
function applyTheme(pref, persist) {
    themePref = THEME_PREFS.indexOf(pref) >= 0 ? pref : 'system';
    document.documentElement.setAttribute('data-theme', resolvedTheme(themePref));
    themePaletteCache = null;
    if (persist) { try { localStorage.setItem(THEME_KEY, themePref); } catch (e) { } }
    document.querySelectorAll('input[name="theme"]').forEach(input => { input.checked = input.value === themePref; });
}
function initTheme() {
    applyTheme(themePref, false);
    document.querySelectorAll('input[name="theme"]').forEach(input => {
        input.addEventListener('change', () => { if (input.checked) applyTheme(input.value, true); });
    });
    if (!systemTheme) return;
    const followSystem = () => { if (themePref === 'system') applyTheme('system', false); };
    if (systemTheme.addEventListener) systemTheme.addEventListener('change', followSystem);
    else if (systemTheme.addListener) systemTheme.addListener(followSystem);
}
function badge(t, c) { return `<span class="badge ${c}">${esc(t)}</span>`; }
function get(o, k) { return o?.[k] ?? o?.[k[0].toUpperCase() + k.slice(1)]; }
const inlineFmt = (s) => s.replace(/\*\*(.+?)\*\*/g, '<b>$1</b>').replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\[([^\]]+)\]\((https?:\/\/[^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>');
function renderMarkdown(md) {
    // Headings arrive one level too high: the author's `#` became the article's
    // <h2> title before this runs, so body `##` is really an <h3> beneath it.
    // Demote each level to keep the outline H1 Guide > H2 article > H3 section.
    const lines = md.replace(/\r\n/g, '\n').split('\n');
    let html = '', listType = null, inTable = false, inCode = false, tableHeader = false;
    const closeList = () => { if (listType) { html += listType === 'ol' ? '</ol>' : '</ul>'; listType = null; } };
    const openList = (t) => { if (listType !== t) { closeList(); html += t === 'ol' ? '<ol>' : '<ul>'; listType = t; } };
    const closeTable = () => { if (inTable) { html += '</tbody></table></div>'; inTable = false; } };
    for (let i = 0; i < lines.length; i++) {
        let line = lines[i];
        if (/^```/.test(line)) {
            if (inCode) { html += '</code></pre>'; inCode = false; }
            else { closeList(); closeTable(); html += '<pre><code>'; inCode = true; }
            continue;
        }
        if (inCode) { html += line + '\n'; continue; }
        if (/^---\s*$/.test(line)) { closeList(); closeTable(); html += '<hr>'; continue; }
        if (/^#\s+/.test(line)) { closeList(); closeTable(); html += `<h2>${inlineFmt(line.replace(/^#\s+/, ''))}</h2>`; continue; }
        if (/^##\s+/.test(line)) { closeList(); closeTable(); html += `<h3>${inlineFmt(line.replace(/^##\s+/, ''))}</h3>`; continue; }
        if (/^###\s+/.test(line)) { closeList(); closeTable(); html += `<h4>${inlineFmt(line.replace(/^###\s+/, ''))}</h4>`; continue; }
        if (/^####\s+/.test(line)) { closeList(); closeTable(); html += `<h5>${inlineFmt(line.replace(/^####\s+/, ''))}</h5>`; continue; }
        const olItem = line.match(/^\d+\.\s+(.*)$/);
        if (olItem) { closeTable(); openList('ol'); html += `<li>${inlineFmt(olItem[1])}</li>`; continue; }
        if (/^\*\s+|^-\s+/.test(line)) { closeTable(); openList('ul'); html += `<li>${inlineFmt(line.replace(/^\*\s+|^-\s+/, ''))}</li>`; continue; }
        closeList();
        if (/^\|/.test(line)) {
            if (line.replace(/\s/g, '').match(/^\|[-:|]+\|$/)) { tableHeader = true; continue; }
            const cells = line.split('|').filter((_, j, a) => j > 0 && j < a.length - 1).map(c => c.trim());
            if (!inTable) { html += '<div class="help-table-wrap"><table><thead><tr>'; html += cells.map(c => `<th scope="col">${inlineFmt(c)}</th>`).join(''); html += '</tr></thead><tbody>'; inTable = true; tableHeader = false; }
            // The row after the separator is the first body row, not a second header.
            else { tableHeader = false; html += '<tr>' + cells.map(c => `<td>${inlineFmt(c)}</td>`).join('') + '</tr>'; }
            continue;
        }
        closeTable();
        if (line.trim() === '') continue;
        if (/^\*.+\*$/.test(line)) { html += `<p><em>${inlineFmt(line.replace(/^\*|\*$/g, ''))}</em></p>`; }
        else { html += `<p>${inlineFmt(line)}</p>`; }
    }
    closeList(); closeTable();
    if (inCode) html += '</code></pre>';
    return html;
}
function reflowWrappedLines(md) {
    const out = [];
    let block = null;
    const flush = () => { if (block !== null) { out.push(block); block = null; } };
    for (const raw of md.replace(/\r\n/g, '\n').split('\n')) {
        const line = raw.trim();
        if (line === '' || /^#{1,6}\s/.test(line) || /^```/.test(line) || /^\|/.test(line) || /^-{3,}$/.test(line)) {
            flush(); out.push(line); continue;
        }
        if (/^([-*]|\d+\.)\s/.test(line)) { flush(); block = line; continue; }
        block = block === null ? line : block + ' ' + line;
    }
    flush();
    return out.join('\n');
}

// ---- OpcBridge Logic ---------------------------------------------------------------
// The editor keeps a draft object and reads the form back on save; the 1 s refresh only
// repaints the live chips, never the inputs, so typing is not disturbed. Definitions and
// evaluated state are cached in the browser so an unreachable bridge keeps the last known
// blocks readable (editing is disabled until it is back).
const state = {
    // The bridge's mapped tags (/api/hmi/tags): the source and tag pickers read this.
    tags: [],
    logic: [],
    logicDraft: null,
    logicSelectedId: '',
    logicStateById: {},
    // The Result view's own selection, kept apart from the editor draft.
    logicResultId: '',
    activeView: 'editor',
    logicOffline: null,
    bridgeBaseUrl: ''
};
const DEFS_KEY = 'opcbridge.logic.defs';
const STATE_KEY = 'opcbridge.logic.state';
const TAGS_KEY = 'opcbridge.logic.tags';
let defaultLogicMessage = '';

function cacheRead(key) {
    try {
        const raw = localStorage.getItem(key);
        return raw ? JSON.parse(raw) : null;
    } catch (e) {
        return null;
    }
}
function cacheWrite(key, value) {
    try { localStorage.setItem(key, JSON.stringify(value)); } catch (e) { }
}
function setBridgeOnline(ok, offlineMessage) {
    if (state.logicOffline === !ok) return;
    state.logicOffline = !ok;
    const dot = el('bridgeDot');
    if (dot) dot.classList.toggle('off', !ok);
    const badge = el('bridgeStateBadge');
    if (badge) {
        badge.textContent = ok ? 'connected' : 'unreachable';
        badge.className = 'badge ' + (ok ? 'good' : 'bad');
    }
    const save = el('btnLogicSave');
    if (save) save.disabled = !ok;
    if (el('logicMessage')) el('logicMessage').textContent = ok ? defaultLogicMessage : (offlineMessage || defaultLogicMessage);
    if (ok) refreshBridgeStatus();
}
async function refreshBridgeStatus() {
    try {
        const p = await (await fetch('/api/bridge/status', { cache: 'no-store' })).json();
        state.bridgeBaseUrl = p.baseUrl || '';
        const address = el('bridgeAddress');
        if (address) address.textContent = p.baseUrl ? p.baseUrl.replace(/^https?:\/\//, '') : 'not found';
    } catch (e) { }
}
function openLogicBlocks() {
    const dialog = el('logicBlocksDialog');
    if (dialog && !dialog.open) dialog.showModal();
}
function closeLogicBlocks() {
    const dialog = el('logicBlocksDialog');
    if (dialog && dialog.open) dialog.close();
}
function openGuide() {
    const dialog = el('guideDialog');
    if (dialog && !dialog.open) dialog.showModal();
    loadGuide();
}
function closeGuide() {
    const dialog = el('guideDialog');
    if (dialog && dialog.open) dialog.close();
}
// ---- Editor / Result views ---------------------------------------------------------------
// The two panels stay in the DOM and switching only flips classes, so nothing is rebuilt
// while someone is typing. The hash carries the view (#/editor, #/result) the way the bridge
// dashboard carries its route, and a real move lands focus on the new view's heading.
function showView(name, focus) {
    const result = name === 'result';
    state.activeView = result ? 'result' : 'editor';
    el('view-logic').classList.toggle('active', !result);
    el('view-result').classList.toggle('active', result);
    document.querySelectorAll('.view-tab').forEach(tab => {
        const on = (tab.dataset.view === 'result') === result;
        tab.classList.toggle('active', on);
        tab.setAttribute('aria-selected', String(on));
        tab.tabIndex = on ? 0 : -1;
    });
    if (result) renderLogicResult();
    try { history.replaceState(null, '', '#/' + state.activeView); } catch (e) { }
    if (!focus) return;
    document.querySelector('.content').scrollTop = 0;
    const title = document.querySelector('#' + (result ? 'view-result' : 'view-logic') + ' .view-title');
    if (title) title.focus({ preventScroll: true });
}
// A tab strip is one Tab stop: arrows walk it and switch as they land.
function onViewTabKey(event) {
    const tabs = Array.from(document.querySelectorAll('.view-tab'));
    const index = tabs.indexOf(document.activeElement);
    if (index < 0) return;
    let next = null;
    if (event.key === 'ArrowRight') next = tabs[(index + 1) % tabs.length];
    else if (event.key === 'ArrowLeft') next = tabs[(index - 1 + tabs.length) % tabs.length];
    else if (event.key === 'Home') next = tabs[0];
    else if (event.key === 'End') next = tabs[tabs.length - 1];
    if (!next) return;
    event.preventDefault();
    showView(next.dataset.view, false);
    next.focus();
}
let guideLoaded = false;
let notesLoaded = false;
async function loadGuide() {
    if (guideLoaded) return;
    guideLoaded = true;
    try {
        const p = await (await fetch('/api/help', { cache: 'no-store' })).json();
        const sections = (p.markdown || '').split(/\r?\n---\r?\n/).filter(section => section.trim());
        const items = sections.map((section, i) => {
            const titleMatch = section.match(/^#\s+(.+)/m);
            return {
                title: titleMatch ? titleMatch[1] : 'Section ' + (i + 1),
                body: renderMarkdown(section.replace(/^#\s+.+/m, ''))
            };
        });
        el('guideToc').innerHTML = items.map((item, i) =>
            `<button type="button" data-guide-topic="${i}"${i === 0 ? ' class="active" aria-current="true"' : ''}>${esc(item.title)}</button>`).join('');
        el('guidePane').innerHTML = items.map((item, i) =>
            `<article class="guide-article${i === 0 ? ' active' : ''}" data-guide-article="${i}"><h2 class="help-article-title">${esc(item.title)}</h2><div class="help-body">${item.body}</div></article>`).join('');
    } catch (e) {
        el('guidePane').innerHTML = '<p class="msg">The guide could not be loaded.</p>';
    }
}
async function loadNotes() {
    if (notesLoaded) return;
    notesLoaded = true;
    try {
        const p = await (await fetch('/api/changelog', { cache: 'no-store' })).json();
        el('notesBody').innerHTML = renderMarkdown(reflowWrappedLines(p.markdown || ''));
    } catch (e) {
        el('notesBody').innerHTML = '<p class="msg">The release notes could not be loaded.</p>';
    }
}
function showGuideTab(tab) {
    const guide = tab === 'guide';
    el('guideWrap').hidden = !guide;
    el('notesWrap').hidden = guide;
    el('btnGuideTab').setAttribute('aria-pressed', String(guide));
    el('btnNotesTab').setAttribute('aria-pressed', String(!guide));
    if (guide) loadGuide(); else loadNotes();
}
function showGuideTopic(index) {
    document.querySelectorAll('#guideToc button').forEach((button, i) => {
        const active = i === index;
        button.classList.toggle('active', active);
        if (active) button.setAttribute('aria-current', 'true'); else button.removeAttribute('aria-current');
    });
    document.querySelectorAll('#guidePane .guide-article').forEach((article, i) => {
        article.classList.toggle('active', i === index);
    });
}
async function loadVersion() {
    try {
        const p = await (await fetch('/api/version', { cache: 'no-store' })).json();
        el('appVersion').textContent = 'v' + (p.version || '');
    } catch (e) { }
}

// ---- Plant logic (Tags ▸ Logic) ---------------------------------------------------------
// Blocks are authored here and evaluated by the bridge (/api/logic/state); the mobile app
// renders the same state. The editor keeps a draft object and reads the form back on save;
// the 1 s refresh only repaints the live chips, never the inputs, so typing is not disturbed.
const LOGIC_OPS = [['on', 'ON (NO contact)'], ['off', 'OFF (NC contact)'], ['gt', '>'], ['lt', '<'], ['eq', '=']];
function logicNewId() {
    if (window.crypto && window.crypto.randomUUID) return window.crypto.randomUUID();
    // Plain-http LAN pages are not a secure context, so randomUUID can be unavailable.
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
        const r = Math.random() * 16 | 0;
        return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
    });
}
function logicOpNeedsValue(op) { return op === 'gt' || op === 'lt' || op === 'eq'; }
function logicKindLabel(kind) {
    const k = String(kind || 'interlock');
    return k === 'sequence' ? 'Sequence' : k === 'permissive' ? 'Permissive' : 'Interlock';
}
function logicKindOf(block) { return String(get(block, 'kind') || 'interlock'); }
function logicStateOf(blockId) { return state.logicStateById[String(blockId)] || null; }
function logicStateChip(st) {
    if (!st) return '<span class="pill" style="padding:1px 6px;font-size:var(--fs-micro)">no state</span>';
    const v = String(get(st, 'state') || 'unknown');
    const cls = v === 'ready' ? 'good' : v === 'blocked' ? 'bad' : v === 'disabled' ? '' : 'partial';
    return badge(v, cls);
}
function logicSourceList() {
    // The pickers read the bridge's tag snapshot (/api/hmi/tags): a source is a source
    // because it has mapped tags, and a tag's Writeable flag gates the action pickers.
    const seen = new Map();
    (state.tags || []).forEach(tag => {
        const id = String(get(tag, 'sourceId') || '');
        if (id && !seen.has(id)) seen.set(id, String(get(tag, 'sourceName') || id));
    });
    return Array.from(seen, pair => ({ sourceId: pair[0], displayName: pair[1] }));
}
function logicSourceOptions(selected) {
    const sources = logicSourceList();
    return '<option value="">— source —</option>' + sources.map(s =>
        `<option value="${attr(s.sourceId)}"${s.sourceId === selected ? ' selected' : ''}>${esc(s.displayName || s.sourceId)}</option>`).join('');
}
function logicTagOptions(sourceId, selected, wantWrite) {
    if (!sourceId) return '<option value="">— tag —</option>';
    const rows = (state.tags || []).filter(tag => String(get(tag, 'sourceId') || '') === sourceId);
    const options = rows.map(tag => {
        const item = String(get(tag, 'itemId') || '');
        const name = String(get(tag, 'displayName') || item);
        const usable = !wantWrite || get(tag, 'writeable') === true;
        return `<option value="${attr(item)}"${item === selected ? ' selected' : ''}${usable ? '' : ' disabled'}>${esc(name)}${usable ? '' : ' — needs write'}</option>`;
    }).join('');
    return '<option value="">— tag —</option>' + options;
}

function logicConditionRow(condition, stepId) {
    const op = String(condition.op || 'on');
    const needsValue = logicOpNeedsValue(op);
    const conditionId = condition.id || logicNewId();
    const severity = String(condition.severity || 'block');
    const holdSeconds = condition.holdMs ? String(condition.holdMs / 1000) : '';
    // OR groups are a property of interlock / permissive blocks; a sequence step is already a
    // list of conditions that must all be true, so its rows carry no group box.
    const groupField = stepId
        ? ''
        : `<input type="text" data-field="group" placeholder="OR group" maxlength="24" style="width:110px" title="OR group (the OR gate): conditions sharing this label are combined with any-of, so one true member satisfies the group. The block still needs every group and every ungrouped condition." value="${attr(condition.group || '')}">`;
    return `<div class="logic-cond-row" data-condition-id="${attr(conditionId)}">
        <input type="text" data-field="text" placeholder="Operator sentence (e.g. Line 01 start permit must be given)" maxlength="200" value="${attr(condition.text || '')}">
        <select data-field="source">${logicSourceOptions(condition.sourceId || '')}</select>
        <select data-field="item">${logicTagOptions(condition.sourceId || '', condition.itemId || '', false)}</select>
        <select data-field="op" title="ON = normally open contact (should read 1); OFF = normally closed contact (should read 0).">${LOGIC_OPS.map(([v, label]) => `<option value="${v}"${v === op ? ' selected' : ''}>${label}</option>`).join('')}</select>
        <input type="number" step="any" data-field="value" placeholder="value" value="${condition.value === null || condition.value === undefined ? '' : attr(String(condition.value))}" style="${needsValue ? '' : 'display:none'}">
        ${groupField}
        <input type="number" data-field="hold" step="0.1" min="0" max="3600" placeholder="hold s" style="width:80px" title="Hold timer in seconds (0 = none): the contact counts as satisfied only after its input has held true this long." value="${attr(holdSeconds)}">
        <select data-field="severity"><option value="block"${severity === 'block' ? ' selected' : ''}>blocks</option><option value="warn"${severity === 'warn' ? ' selected' : ''}>warn only</option></select>
        <input type="text" data-field="nextStep" placeholder="What to do when not true" maxlength="200" value="${attr(condition.nextStepText || '')}">
        <span class="logic-live" data-logic-condition-live="${attr(conditionId)}">—</span>
        <button class="btn ghost" type="button" data-action="logic-remove-condition" title="Remove condition">✕</button>
    </div>`;
}
function logicStepCard(step, index, total) {
    const stepId = step.id || logicNewId();
    const conditions = (step.conditions || []).map(c => logicConditionRow(c, stepId)).join('');
    return `<div class="logic-step" data-step-id="${attr(stepId)}">
        <div class="logic-step-head">
            <span class="msg" style="font-size:var(--fs-micro)">STEP ${index + 1}</span>
            <input type="text" data-field="stepName" placeholder="Step name" maxlength="64" value="${attr(step.name || '')}">
            <span class="logic-live" data-logic-step-live="${attr(stepId)}">—</span>
            <button class="btn ghost" type="button" data-action="logic-step-up"${index === 0 ? ' disabled' : ''} title="Move earlier">↑</button>
            <button class="btn ghost" type="button" data-action="logic-step-down"${index === total - 1 ? ' disabled' : ''} title="Move later">↓</button>
            <button class="btn ghost" type="button" data-action="logic-step-remove" title="Remove step">✕</button>
        </div>
        <input type="text" data-field="stepNextStep" placeholder="What to do in this step (next-step text)" maxlength="200" value="${attr(step.nextStepText || '')}" style="width:100%">
        <div class="logic-step-conditions">${conditions || '<span class="msg">No conditions — a step with none completes immediately.</span>'}</div>
        <div class="tag-browser-toolbar" style="margin-top:6px">
            <button class="btn ghost" type="button" data-action="logic-add-step-condition">+ Condition</button>
            <span class="msg" data-logic-step-reason="${attr(stepId)}"></span>
        </div>
        <div class="tag-browser-toolbar" style="margin-top:4px">
            <span class="msg">Completion handshake <span class="info" data-tip="Optional. When set, the step counts as done only once this tag is true — the PLC telling the bridge the step finished. Leave both blank to complete the step as soon as its conditions are true.">i</span></span>
            <select data-field="completionSource">${logicSourceOptions(step.completionSourceId || '')}</select>
            <select data-field="completionItem">${logicTagOptions(step.completionSourceId || '', step.completionItemId || '', false)}</select>
        </div>
    </div>`;
}
function logicActionRow(action) {
    return `<div class="logic-action-row">
        <input type="text" data-field="label" placeholder="Button label (e.g. Start pump)" maxlength="32" value="${attr(action.label || '')}">
        <select data-field="source">${logicSourceOptions(action.sourceId || '')}</select>
        <select data-field="item">${logicTagOptions(action.sourceId || '', action.itemId || '', true)}</select>
        <input type="text" data-field="value" placeholder="value to write" value="${attr(action.value || '')}">
        <label class="msg" style="display:flex;align-items:center;gap:4px"><input type="checkbox" data-field="confirm"${action.confirm !== false ? ' checked' : ''}> confirm</label>
        <button class="btn ghost" type="button" data-action="logic-remove-action" title="Remove action">✕</button>
    </div>`;
}
// ---- IEC 61131-3 network editor ---------------------------------------------------------
// The network is authored as an indented row list: on save a row's inputs are the rows below
// it at a deeper indent, and the outermost rows are the block's roots (all must be true). The
// bridge evaluates the network; the phone lists every element with its own live state.
const LOGIC_ELEMENT_KINDS = [
    ['contact', 'Contact — NO / NC / compare'],
    ['and', 'AND'], ['or', 'OR'], ['xor', 'XOR'], ['not', 'NOT'],
    ['ton', 'TON — on-delay (IN, PT)'],
    ['tof', 'TOF — off-delay (IN, PT)'],
    ['tp', 'TP — pulse (IN, PT)'],
    ['ctu', 'CTU — up counter (CU, R, PV)'],
    ['ctd', 'CTD — down counter (CD, LD, PV)'],
    ['sr', 'SR — set-dominant latch (S1, R)'],
    ['rs', 'RS — reset-dominant latch (S, R1)'],
    ['r_trig', 'R_TRIG — rising edge (IN)'],
    ['f_trig', 'F_TRIG — falling edge (IN)']
];
function logicElementKindOf(element) { return String(get(element, 'kind') || 'contact'); }
function logicKindOptions(selected) {
    return LOGIC_ELEMENT_KINDS
        .map(pair => '<option value="' + attr(pair[0]) + '"' + (pair[0] === selected ? ' selected' : '') + '>' + esc(pair[1]) + '</option>')
        .join('');
}
function logicOpOptions(selected) {
    return LOGIC_OPS
        .map(pair => '<option value="' + attr(pair[0]) + '"' + (pair[0] === selected ? ' selected' : '') + '>' + esc(pair[1]) + '</option>')
        .join('');
}
function logicElementIsTimer(kind) { return kind === 'ton' || kind === 'tof' || kind === 'tp'; }
function logicElementIsCounter(kind) { return kind === 'ctu' || kind === 'ctd'; }
function logicElementRow(element, indent) {
    const kind = logicElementKindOf(element);
    const id = element.id || logicNewId();
    const op = String(element.op || 'on');
    const needsValue = logicOpNeedsValue(op);
    const severity = String(element.severity || 'block');
    const fields = [];
    if (kind === 'contact') {
        fields.push(`<input type="text" data-field="text" class="logic-element-label" placeholder="Operator sentence" maxlength="200" value="${attr(element.text || '')}">`);
        fields.push(`<select data-field="source">${logicSourceOptions(element.sourceId || '')}</select>`);
        fields.push(`<select data-field="item">${logicTagOptions(element.sourceId || '', element.itemId || '', false)}</select>`);
        fields.push(`<select data-field="op" title="NO = normally open (should read 1); NC = normally closed (should read 0).">${logicOpOptions(op)}</select>`);
        fields.push(`<input type="number" step="any" data-field="value" placeholder="value" value="${element.value === null || element.value === undefined ? '' : attr(String(element.value))}" style="${needsValue ? '' : 'display:none'}">`);
    } else if (logicElementIsTimer(kind)) {
        const pt = element.ptMs ? String(element.ptMs / 1000) : '';
        fields.push(`<input type="text" data-field="text" class="logic-element-label" placeholder="Label (e.g. Flow must hold)" maxlength="200" value="${attr(element.text || '')}">`);
        fields.push(`<input type="number" data-field="pt" step="0.1" min="0" max="3600" placeholder="PT s" style="width:80px" title="Preset time (PT) in seconds." value="${attr(pt)}">`);
    } else if (logicElementIsCounter(kind)) {
        fields.push(`<input type="text" data-field="text" class="logic-element-label" placeholder="Label (e.g. Start attempts)" maxlength="200" value="${attr(element.text || '')}">`);
        fields.push(`<input type="number" data-field="pv" min="0" max="1000000" placeholder="PV" style="width:80px" title="Preset count (PV)." value="${element.pv === null || element.pv === undefined ? '' : attr(String(element.pv))}">`);
    } else {
        fields.push(`<input type="text" data-field="text" class="logic-element-label" placeholder="Label (optional)" maxlength="200" value="${attr(element.text || '')}">`);
    }
    fields.push(`<select data-field="severity"><option value="block"${severity === 'block' ? ' selected' : ''}>blocks</option><option value="warn"${severity === 'warn' ? ' selected' : ''}>warn only</option></select>`);
    fields.push(`<span class="logic-live" data-logic-condition-live="${attr(id)}">—</span>`);
    return `<div class="logic-cond-row logic-element-row" data-element-id="${attr(id)}" data-indent="${indent}" style="padding-left:${8 + indent * 18}px">
        <select data-field="kind">${logicKindOptions(kind)}</select>
        ${fields.join('')}
        <button class="btn ghost" type="button" data-action="logic-element-indent" title="Make this row an input of the row above">→</button>
        <button class="btn ghost" type="button" data-action="logic-element-outdent" title="Move this row out one level">←</button>
        <button class="btn ghost" type="button" data-action="logic-element-remove" title="Remove element">✕</button>
    </div>`;
}
function logicRenderElementRows(container, elements, indent) {
    (elements || []).forEach(element => {
        container.insertAdjacentHTML('beforeend', logicElementRow(element, indent));
        logicRenderElementRows(container, get(element, 'inputs'), indent + 1);
    });
}
function logicEventFromRow(row) {
    const field = name => { const node = row.querySelector(`[data-field="${name}"]`); return node ? node.value : ''; };
    const kind = field('kind') || 'contact';
    const op = field('op') || 'on';
    const rawValue = field('value');
    const pt = Number(field('pt'));
    const pv = Number(field('pv'));
    const element = {
        id: row.dataset.elementId || logicNewId(),
        kind: kind,
        text: field('text').trim(),
        severity: field('severity') || 'block',
        inputs: []
    };
    if (kind === 'contact') {
        element.sourceId = field('source');
        element.itemId = field('item');
        element.op = op;
        element.value = logicOpNeedsValue(op) && rawValue !== '' ? Number(rawValue) : null;
    } else if (logicElementIsTimer(kind)) {
        element.ptMs = Number.isFinite(pt) && pt > 0 ? Math.round(pt * 1000) : 0;
    } else if (logicElementIsCounter(kind)) {
        element.pv = Number.isFinite(pv) && pv > 0 ? Math.round(pv) : 0;
    }
    return element;
}
function logicElementTreeFromDom() {
    const rows = Array.from(el('logicElements').querySelectorAll('.logic-element-row'))
        .map(row => ({ indent: Number(row.dataset.indent || 0), element: logicEventFromRow(row) }));
    let position = 0;
    const build = parentIndent => {
        const list = [];
        while (position < rows.length && rows[position].indent > parentIndent) {
            const node = rows[position++];
            node.element.inputs = build(node.indent);
            list.push(node.element);
        }
        return list;
    };
    return build(-1);
}
function logicRenderNetwork() {
    const container = el('logicElements');
    const elements = get(state.logicDraft || {}, 'elements') || [];
    if (!elements.length) {
        container.innerHTML = '<span class="msg">No elements — add the first one, then indent the rows under it.</span>';
        return;
    }
    container.innerHTML = '';
    logicRenderElementRows(container, elements, 0);
}
/** Rewrites the simple conditions as the equivalent network (the bridge expands them the same way). */
function logicExpandConditions(conditions) {
    const roots = [];
    const groups = {};
    (conditions || []).forEach(condition => {
        let element = {
            id: condition.id || logicNewId(),
            kind: 'contact',
            text: condition.text,
            sourceId: condition.sourceId,
            itemId: condition.itemId,
            op: condition.op || 'on',
            value: condition.value === undefined ? null : condition.value,
            severity: condition.severity || 'block',
            inputs: []
        };
        if ((condition.holdMs || 0) > 0) {
            element = {
                id: logicNewId(),
                kind: 'ton',
                text: condition.text,
                ptMs: condition.holdMs,
                severity: condition.severity || 'block',
                inputs: [element]
            };
        }
        const group = String(condition.group || '').trim();
        if (!group) { roots.push(element); return; }
        const key = group.toLowerCase();
        if (!groups[key]) {
            groups[key] = { id: logicNewId(), kind: 'or', text: group, severity: 'block', inputs: [] };
            roots.push(groups[key]);
        }
        groups[key].inputs.push(element);
    });
    if (roots.length <= 1) return roots;
    return [{ id: logicNewId(), kind: 'and', text: '', severity: 'block', inputs: roots }];
}
function logicElementLiveRow(row) {
    const indent = Number(row.dataset.indent || 0);
    row.outerHTML = logicElementRow(logicEventFromRow(row), indent);
}
function newLogicDraft() {
    return {
        id: '',
        name: '',
        group: '',
        description: '',
        kind: 'interlock',
        enabled: true,
        order: (state.logic || []).length,
        tags: [],
        elements: [],
        conditions: [{ id: logicNewId(), text: '', sourceId: '', itemId: '', op: 'on', value: null, group: '', holdMs: 0, nextStepText: null, severity: 'block' }],
        steps: [],
        actions: []
    };
}
function logicDraftConditionFrom(row) {
    const field = name => { const node = row.querySelector(`[data-field="${name}"]`); return node ? node.value : ''; };
    const op = field('op') || 'on';
    const rawValue = field('value');
    const holdSeconds = Number(field('hold'));
    return {
        id: row.dataset.conditionId || logicNewId(),
        text: field('text').trim(),
        sourceId: field('source'),
        itemId: field('item'),
        op: op,
        value: logicOpNeedsValue(op) && rawValue !== '' ? Number(rawValue) : null,
        group: field('group').trim(),
        holdMs: Number.isFinite(holdSeconds) && holdSeconds > 0 ? Math.round(holdSeconds * 1000) : 0,
        nextStepText: field('nextStep').trim() || null,
        severity: field('severity') || 'block'
    };
}
function logicDraftActionFrom(row) {
    const field = name => { const node = row.querySelector(`[data-field="${name}"]`); return node ? node.value : ''; };
    const confirmNode = row.querySelector('[data-field="confirm"]');
    return {
        label: field('label').trim(),
        sourceId: field('source'),
        itemId: field('item'),
        value: field('value').trim(),
        confirm: confirmNode ? confirmNode.checked : true
    };
}
function collectLogicDraft() {
    const block = state.logicDraft ? Object.assign({}, state.logicDraft) : newLogicDraft();
    const stored = (state.logic || []).find(b => String(get(b, 'id') || '') === String(block.id || ''));
    block.name = el('lgName').value.trim();
    block.kind = el('lgKind').value;
    block.enabled = !!el('lgEnabled').checked;
    block.description = el('lgDescription').value.trim();
    block.group = el('lgGroup').value.trim();
    // Comma-separated labels; the store trims, drops blanks and deduplicates, and rejects an over-long list.
    block.tags = el('lgTags').value.split(',').map(tag => tag.trim()).filter(tag => tag.length > 0);
    block.order = stored ? Number(get(stored, 'order') || 0) : (state.logic || []).length;
    block.actions = Array.from(el('logicActions').querySelectorAll('.logic-action-row')).map(logicDraftActionFrom);
    if (block.kind === 'sequence') {
        block.conditions = [];
        block.elements = [];
        block.steps = Array.from(el('logicSteps').querySelectorAll('.logic-step')).map(stepEl => {
            const stepId = stepEl.dataset.stepId || logicNewId();
            const storedStep = stored && (stored.steps || []).find(s => String(get(s, 'id') || '') === String(stepId));
            return {
                id: stepId,
                name: (stepEl.querySelector('[data-field="stepName"]') ? stepEl.querySelector('[data-field="stepName"]').value : '').trim(),
                nextStepText: (stepEl.querySelector('[data-field="stepNextStep"]') ? stepEl.querySelector('[data-field="stepNextStep"]').value : '').trim() || null,
                completionSourceId: (stepEl.querySelector('[data-field="completionSource"]') ? stepEl.querySelector('[data-field="completionSource"]').value : '') || null,
                completionItemId: (stepEl.querySelector('[data-field="completionItem"]') ? stepEl.querySelector('[data-field="completionItem"]').value : '') || null,
                // A step carries either conditions or a network; the editor writes conditions.
                elements: [],
                conditions: Array.from(stepEl.querySelectorAll('.logic-cond-row')).map(logicDraftConditionFrom),
                // Step buttons are not authored here yet; keep whatever the API stored.
                actions: storedStep ? (storedStep.actions || []) : []
            };
        });
    } else if (el('logicElements').querySelectorAll('.logic-element-row').length > 0) {
        // The block carries an IEC network: the rows are the network, the flat list is empty.
        block.elements = logicElementTreeFromDom();
        block.conditions = [];
        block.steps = [];
    } else {
        block.elements = [];
        block.steps = [];
        block.conditions = Array.from(el('logicConditions').querySelectorAll('.logic-cond-row')).map(logicDraftConditionFrom);
    }
    return block;
}
function logicDeepCopy(value) { return JSON.parse(JSON.stringify(value)); }
/** The condition rows as they stand in the editor, for the network conversion. */
function collectCurrentConditions() {
    return Array.from(el('logicConditions').querySelectorAll('.logic-cond-row')).map(logicDraftConditionFrom);
}
function selectLogicBlock(blockId) {
    const found = (state.logic || []).find(b => String(get(b, 'id') || '') === String(blockId));
    state.logicSelectedId = found ? String(get(found, 'id')) : '';
    state.logicDraft = found ? logicDeepCopy(found) : newLogicDraft();
    el('logicEditorMsg').textContent = '';
    renderLogicView();
    closeLogicBlocks();
    document.querySelector('.content').scrollTop = 0;
}
function addLogicBlock() {
    state.logicSelectedId = '';
    state.logicDraft = newLogicDraft();
    el('logicEditorMsg').textContent = '';
    renderLogicView();
    closeLogicBlocks();
    document.querySelector('.content').scrollTop = 0;
    el('lgName').focus();
}
function renderLogicKindUi() {
    const kind = el('lgKind').value;
    const hasNetwork = (get(state.logicDraft || {}, 'elements') || []).length > 0;
    const sequence = kind === 'sequence';
    el('logicStepsBlock').style.display = sequence ? '' : 'none';
    el('logicConditionsBlock').style.display = sequence || hasNetwork ? 'none' : '';
    el('logicNetworkBlock').style.display = !sequence && hasNetwork ? '' : 'none';
}
function renderLogicEditor() {
    const block = state.logicDraft || newLogicDraft();
    const kind = logicKindOf(block);
    el('logicEditorTitle').textContent = get(block, 'id') ? (get(block, 'name') || 'Block') : 'New block';
    el('lgName').value = get(block, 'name') || '';
    el('lgKind').value = kind;
    el('lgEnabled').checked = get(block, 'enabled') !== false;
    el('lgDescription').value = get(block, 'description') || '';
    el('lgGroup').value = get(block, 'group') || '';
    el('lgTags').value = (get(block, 'tags') || []).join(', ');
    const conditions = block.conditions || [];
    el('logicConditions').innerHTML = conditions.length
        ? conditions.map(c => logicConditionRow(c, null)).join('')
        : '<span class="msg">No conditions — add at least one.</span>';
    logicRenderNetwork();
    const steps = block.steps || [];
    el('logicSteps').innerHTML = steps.length
        ? steps.map((s, i) => logicStepCard(s, i, steps.length)).join('')
        : '<span class="msg">No steps — add at least one.</span>';
    const actions = block.actions || [];
    el('logicActions').innerHTML = actions.length
        ? actions.map(logicActionRow).join('')
        : '<span class="msg">No actions — optional write buttons for the phone.</span>';
    renderLogicKindUi();
}
function renderLogicView() {
    const blocks = state.logic || [];
    if (!state.logicDraft) {
        state.logicDraft = blocks.length ? logicDeepCopy(blocks[0]) : newLogicDraft();
        state.logicSelectedId = blocks.length ? String(get(blocks[0], 'id') || '') : '';
    } else if (get(state.logicDraft, 'id') && !blocks.some(b => String(get(b, 'id') || '') === String(get(state.logicDraft, 'id')))) {
        // The edited block is gone (deleted elsewhere): start clean rather than resurrect it.
        state.logicDraft = newLogicDraft();
        state.logicSelectedId = '';
    }
    el('logicCount').textContent = blocks.length ? blocks.length + (blocks.length === 1 ? ' block' : ' blocks') : 'No blocks';
    // The list follows the phone: heading per interlock group. Blocks without a group stay in
    // authored order at the top; a grouped list sorts by group so each heading appears once.
    const anyGrouped = blocks.some(block => String(get(block, 'group') || '').trim().length > 0);
    const listed = anyGrouped
        ? blocks.slice().sort((a, b) => {
            const ga = String(get(a, 'group') || '').trim();
            const gb = String(get(b, 'group') || '').trim();
            return ga.localeCompare(gb) || (Number(get(a, 'order') || 0) - Number(get(b, 'order') || 0));
        })
        : blocks;
    let lastGroup = null;
    el('logicBlockList').innerHTML = listed.length ? listed.map(block => {
        const group = String(get(block, 'group') || '').trim();
        let head = '';
        if (anyGrouped && group !== lastGroup) {
            head = `<div class="logic-group-head">${esc(group || 'Ungrouped')}</div>`;
        }
        lastGroup = group;
        const id = String(get(block, 'id') || '');
        const active = id !== '' && id === state.logicSelectedId;
        const reason = logicStateOf(id);
        return head + `<div class="logic-block-item">
            <button type="button" class="logic-block-row${active ? ' active' : ''}" data-action="logic-select" data-block-id="${attr(id)}">
                <span class="logic-row-name">${esc(get(block, 'name') || '(unnamed)')}</span>
                <span class="pill" style="padding:1px 6px;font-size:var(--fs-micro)">${esc(logicKindLabel(get(block, 'kind')))}</span>
                <span data-logic-badge="${attr(id)}">${logicStateChip(reason)}</span>
                <span class="logic-row-reason" data-logic-reason="${attr(id)}">${esc(reason && get(reason, 'reason') ? get(reason, 'reason') : '')}</span>
            </button>
            <button type="button" class="btn ghost logic-block-del" data-action="logic-delete-block" data-block-id="${attr(id)}" title="Delete block">✕</button>
        </div>`;
    }).join('') : '<span class="msg">No logic blocks yet — press + New block.</span>';
    renderLogicEditor();
    renderLogicLive();
}
function renderLogicLive() {
    const states = state.logicStateById || {};
    document.querySelectorAll('#logicBlockList [data-logic-badge]').forEach(node => {
        node.innerHTML = logicStateChip(states[node.dataset.logicBadge]);
    });
    document.querySelectorAll('#logicBlockList [data-logic-reason]').forEach(node => {
        const st = states[node.dataset.logicReason];
        node.textContent = st && get(st, 'reason') ? get(st, 'reason') : '';
    });
    const selected = state.logicSelectedId ? states[state.logicSelectedId] || null : null;
    const liveBadge = el('logicLiveBadge');
    if (liveBadge) liveBadge.innerHTML = selected ? logicStateChip(selected) : '';
    const hint = el('logicStateHint');
    if (hint) {
        if (selected) {
            const reason = get(selected, 'reason');
            hint.textContent = 'Live: ' + get(selected, 'state') + (reason ? ' — ' + reason : '');
        } else {
            hint.textContent = 'Live state appears here for a saved block.';
        }
    }
    const conditionStates = {};
    const stepStates = {};
    const conditionOps = {};
    // Every condition of the simple form and every element of a network, nested inputs included.
    const collectConditions = list => (list || []).forEach(c => { conditionOps[String(get(c, 'id') || '')] = String(get(c, 'op') || 'on'); });
    const collectElements = list => (list || []).forEach(element => {
        const id = String(get(element, 'id') || '');
        const kind = String(get(element, 'kind') || 'contact');
        conditionOps[id] = kind === 'contact' ? String(get(element, 'op') || 'on') : kind;
        collectElements(get(element, 'inputs'));
    });
    (state.logic || []).forEach(block => {
        collectConditions(get(block, 'conditions'));
        collectElements(get(block, 'elements'));
        (get(block, 'steps') || []).forEach(step => {
            collectConditions(get(step, 'conditions'));
            collectElements(get(step, 'elements'));
        });
    });
    Object.keys(states).forEach(blockId => {
        (get(states[blockId], 'elements') || []).forEach(c => { conditionStates[String(get(c, 'id') || '')] = c; });
        (get(states[blockId], 'steps') || []).forEach(s => { stepStates[String(get(s, 'id') || '')] = s; });
    });
    document.querySelectorAll('[data-logic-condition-live]').forEach(node => {
        const c = conditionStates[node.dataset.logicConditionLive];
        if (!c) { node.textContent = '—'; node.className = 'logic-live'; node.title = ''; return; }
        const s = String(get(c, 'state') || 'unknown');
        const value = get(c, 'valueText');
        // A bit reads as 1 or 0; a numeric condition keeps its value (a level above 50 % is not a bit).
        const op = conditionOps[node.dataset.logicConditionLive] || 'on';
        const isBoolean = op === 'on' || op === 'off';
        node.textContent = isBoolean
            ? (s === 'true' ? '1' : s === 'false' ? '0' : 'no data')
            : (value ? value : 'no data');
        node.className = 'logic-live ' + (s === 'true' ? 'true' : s === 'false' ? 'false' : 'unknown');
        node.title = isBoolean
            ? (value ? 'Live value: ' + value : '')
            : 'Condition: ' + s + (value ? ' · live value: ' + value : '');
    });
    document.querySelectorAll('#logicSteps .logic-step').forEach(stepEl => {
        const s = stepStates[stepEl.dataset.stepId];
        stepEl.classList.toggle('current', !!s && String(get(s, 'state') || '') === 'current');
    });
    document.querySelectorAll('[data-logic-step-live]').forEach(node => {
        const s = stepStates[node.dataset.logicStepLive];
        const v = s ? String(get(s, 'state') || '') : '';
        node.textContent = v || '—';
        node.className = 'logic-live ' + (v === 'done' ? 'true' : v === 'current' ? 'false' : 'unknown');
    });
    document.querySelectorAll('[data-logic-step-reason]').forEach(node => {
        const s = stepStates[node.dataset.logicStepReason];
        node.textContent = s && get(s, 'reason') ? get(s, 'reason') : '';
    });
}
// ---- Result view: the evaluated configuration ---------------------------------------------
// The read-only counterpart of the editor: every block with its live state on the left, the
// selected block's network and steps on the right — the same result the phone presents
// (should 1 / actual 0, timer and counter progress, step states). It renders from
// state.logic + state.logicStateById only; the editor's draft is never touched.
function logicResultState(block) { return state.logicStateById[String(get(block, 'id') || '')] || null; }
function logicResultKindLabel(st, def) {
    const kind = String(get(st, 'kind') || 'contact');
    if (kind === 'contact') {
        const op = String(get(def || {}, 'op') || '');
        if (op) return op === 'on' ? 'NO' : op === 'off' ? 'NC' : op === 'gt' ? '>' : op === 'lt' ? '<' : '=';
        const should = String(get(st, 'should') || '');
        return should === '0' ? 'NC' : should === '1' ? 'NO' : should ? should.charAt(0) : 'NO';
    }
    const labels = { and: 'AND', or: 'OR', xor: 'XOR', not: 'NOT', ton: 'TON IN', tof: 'TOF IN', tp: 'TP IN', ctu: 'CTU CU', ctd: 'CTD CD', sr: 'SR S1', rs: 'RS S', r_trig: 'R_TRIG IN', f_trig: 'F_TRIG IN' };
    return labels[kind] || kind;
}
function logicResultSeconds(ms) { return (Number(ms || 0) / 1000).toFixed(2).replace(/\.?0+$/, '') + ' s'; }
function logicResultProgress(st, def) {
    const kind = String(get(st, 'kind') || '');
    if (kind === 'ton' || kind === 'tof' || kind === 'tp') {
        const pt = Number(get(st, 'ptMs') || 0);
        if (pt <= 0) return '';
        const verb = kind === 'ton' ? 'holding' : kind === 'tof' ? 'off-delay' : 'pulse';
        if (String(get(st, 'state') || '') === 'true') return verb + ' done · ' + logicResultSeconds(pt);
        return verb + ' ' + logicResultSeconds(get(st, 'elapsedMs')) + ' of ' + logicResultSeconds(pt);
    }
    if (kind === 'ctu' || kind === 'ctd') {
        // The count is the live CV; the preset lives on the definition.
        return 'count ' + Number(get(st, 'count') || 0) + ' of ' + Number(get(def || {}, 'pv') || 0);
    }
    return '';
}
function logicResultActual(st, def) {
    const kind = String(get(st, 'kind') || 'contact');
    if (kind === 'contact') {
        const op = String(get(def || {}, 'op') || (String(get(st, 'should') || '') === '0' ? 'off' : 'on'));
        if (op === 'on' || op === 'off') {
            const matches = get(st, 'matches');
            if (matches === null || matches === undefined) return '?';
            const bit = matches ? 1 : 0;
            return String(op === 'on' ? bit : 1 - bit);
        }
        const value = String(get(st, 'valueText') || '');
        return value && value !== '—' ? value : '?';
    }
    const word = String(get(st, 'state') || '');
    return word === 'true' ? '1' : word === 'false' ? '0' : '—';
}
function logicResultShould(st, def) {
    const should = String(get(st, 'should') || '');
    return should ? 'should ' + should + ' · actual ' + logicResultActual(st, def) : '';
}
function logicResultTag(def) {
    const sourceId = String(get(def || {}, 'sourceId') || '');
    const itemId = String(get(def || {}, 'itemId') || '');
    if (!itemId) return '';
    const tag = (state.tags || []).find(candidate => String(get(candidate, 'sourceId') || '') === sourceId && String(get(candidate, 'itemId') || '') === itemId);
    const display = tag ? String(get(tag, 'displayName') || itemId) : itemId;
    const sourceName = tag ? String(get(tag, 'sourceName') || sourceId) : sourceId;
    return sourceName ? display + ' · ' + sourceName : display;
}
function logicResultDefinitionIndex(block) {
    // The snapshot carries the live values, the definition the sentences, severities and
    // presets; conditions and elements share one id space (a contact keeps its condition's id).
    const index = {};
    const addConditions = list => (list || []).forEach(condition => { index[String(get(condition, 'id') || '')] = condition; });
    const addElements = list => (list || []).forEach(element => {
        index[String(get(element, 'id') || '')] = element;
        addElements(get(element, 'inputs'));
    });
    addConditions(get(block, 'conditions'));
    addElements(get(block, 'elements'));
    (get(block, 'steps') || []).forEach(step => {
        addConditions(get(step, 'conditions'));
        addElements(get(step, 'elements'));
    });
    return index;
}
function logicResultElementRow(st, def) {
    const stateWord = String(get(st, 'state') || 'unknown');
    const mark = stateWord === 'true' ? '✓' : stateWord === 'false' ? '✗' : '—';
    const kindLabel = logicResultKindLabel(st, def);
    const text = def ? String(get(def, 'text') || '') : '';
    const severity = def ? String(get(def, 'severity') || 'block') : 'block';
    const shouldLine = logicResultShould(st, def);
    const shouldKey = shouldLine ? (get(st, 'matches') === true ? 'true' : get(st, 'matches') === false ? 'false' : 'unknown') : '';
    const progress = logicResultProgress(st, def);
    const next = def && get(def, 'nextStepText') && stateWord !== 'true' ? String(get(def, 'nextStepText')) : '';
    const tag = String(get(st, 'kind') || 'contact') === 'contact' && def ? logicResultTag(def) : '';
    const depth = Math.max(0, Number(get(st, 'depth') || 0));
    const value = String(get(st, 'valueText') || '');
    return `<div class="result-el" style="padding-left:${8 + depth * 18}px">
        <span class="result-mark ${esc(stateWord)}">${mark}</span>
        <span class="result-kind">${esc(kindLabel)}</span>
        <span class="result-text">${esc(text)}${severity === 'warn' ? (text ? ' ' : '') + '<span class="result-warn">warn only</span>' : ''}</span>
        <span class="result-actual ${esc(stateWord)}">${esc(logicResultActual(st, def))}</span>
        <span class="result-value">${esc(value)}</span>
        ${shouldLine ? `<span class="result-should ${shouldKey}">${esc(shouldLine)}</span>` : ''}
        ${progress ? `<span class="result-progress">${esc(progress)}</span>` : ''}
        ${tag ? `<span class="result-tag">${esc(tag)}</span>` : ''}
        ${next ? `<span class="result-next">${esc(next)}</span>` : ''}
    </div>`;
}
function logicResultElementRows(elements, index) {
    return (elements || []).map(st => logicResultElementRow(st, index[String(get(st, 'id') || '')] || null)).join('');
}
function logicResultDefinitionIds(step) {
    const ids = {};
    const walk = list => (list || []).forEach(element => {
        ids[String(get(element, 'id') || '')] = true;
        walk(get(element, 'inputs'));
    });
    (get(step, 'conditions') || []).forEach(condition => { ids[String(get(condition, 'id') || '')] = true; });
    walk(get(step, 'elements'));
    return ids;
}
function logicResultStepAssignments(block, elements) {
    // The snapshot pools the block's network with every step's network in step order, depth
    // reset per network. Contacts and authored elements keep their ids; an id-less expansion
    // wrapper (the AND / OR / TON the simple form expands into) belongs to the step of the
    // next known id — a wrapper always precedes what it wraps.
    const stepById = {};
    (get(block, 'steps') || []).forEach((step, index) => {
        Object.keys(logicResultDefinitionIds(step)).forEach(id => { stepById[id] = index; });
    });
    const assignments = (elements || []).map(() => -1);
    let next = -1;
    for (let i = assignments.length - 1; i >= 0; i--) {
        const known = stepById[String(get(elements[i], 'id') || '')];
        if (known !== undefined) next = known;
        assignments[i] = next;
    }
    let previous = -1;
    assignments.forEach((assigned, i) => {
        if (assigned < 0) assignments[i] = previous; else previous = assigned;
    });
    return assignments;
}
function logicResultStepCards(block, st, index) {
    const elements = get(st, 'elements') || [];
    const assignments = logicResultStepAssignments(block, elements);
    const stepStates = {};
    (get(st, 'steps') || []).forEach(stepState => { stepStates[String(get(stepState, 'id') || '')] = stepState; });
    const cards = (get(block, 'steps') || []).map((step, stepIndex) => {
        const stepState = stepStates[String(get(step, 'id') || '')] || null;
        const word = stepState ? String(get(stepState, 'state') || 'unknown') : 'unknown';
        const label = word === 'done' ? 'Done' : word === 'current' ? 'Current' : word === 'pending' ? 'Pending' : 'No data';
        const reason = stepState && get(stepState, 'reason') ? String(get(stepState, 'reason')) : '';
        const rows = elements.filter((element, i) => assignments[i] === stepIndex)
            .map(element => logicResultElementRow(element, index[String(get(element, 'id') || '')] || null)).join('');
        return `<div class="logic-step${word === 'current' ? ' current' : ''}">
            <div class="logic-step-head">
                <span class="result-step-name">${esc(get(step, 'name') || ('Step ' + (stepIndex + 1)))}</span>
                <span class="result-step-state ${esc(word)}">${label}</span>
            </div>
            ${reason ? `<div class="result-next">${esc(reason)}</div>` : ''}
            ${rows || '<span class="msg">No evaluated elements.</span>'}
        </div>`;
    });
    return cards.join('') || '<span class="msg">No steps.</span>';
}
function logicResultSequenceProgress(block, st) {
    const steps = get(block, 'steps') || [];
    if (!steps.length) return '';
    const stepStates = {};
    (get(st || {}, 'steps') || []).forEach(stepState => { stepStates[String(get(stepState, 'id') || '')] = String(get(stepState, 'state') || ''); });
    let done = 0;
    let current = '';
    steps.forEach((step, stepIndex) => {
        const word = stepStates[String(get(step, 'id') || '')] || '';
        if (word === 'done') done++;
        else if (!current && (word === 'current' || word === 'unknown')) current = String(get(step, 'name') || ('Step ' + (stepIndex + 1)));
    });
    if (done === steps.length) return 'complete';
    return done + '/' + steps.length + ' steps' + (current ? ' · ' + current : '');
}
function logicResultBlockRow(block, active) {
    const id = String(get(block, 'id') || '');
    const st = logicResultState(block);
    const word = st ? String(get(st, 'state') || 'unknown') : '';
    const reason = st && get(st, 'reason') ? String(get(st, 'reason')) : '';
    const reasonLine = st ? (word === 'ready' ? 'all conditions met' : reason) : 'reading…';
    const progress = logicKindOf(block) === 'sequence' ? logicResultSequenceProgress(block, st) : '';
    return `<div class="logic-block-item">
        <button type="button" class="logic-block-row${active ? ' active' : ''}" data-result-block-id="${attr(id)}">
            <span class="logic-row-name">${esc(get(block, 'name') || '(unnamed)')}</span>
            <span class="pill" style="padding:1px 6px;font-size:var(--fs-micro)">${esc(logicKindLabel(get(block, 'kind')))}</span>
            <span>${logicStateChip(st)}</span>
            <span class="logic-row-reason">${esc(reasonLine)}</span>
            ${progress ? `<span class="logic-row-reason">${esc(progress)}</span>` : ''}
        </button>
    </div>`;
}
function logicResultSummaryText() {
    const blocks = state.logic || [];
    if (!blocks.length) return 'no logic blocks configured';
    let ready = 0, blocked = 0, noData = 0;
    blocks.forEach(block => {
        const st = logicResultState(block);
        const word = st ? String(get(st, 'state') || '') : 'unknown';
        if (word === 'ready') ready++;
        else if (word === 'blocked') blocked++;
        else if (word !== 'disabled') noData++;
    });
    return ready + ' ready · ' + blocked + ' blocked' + (noData ? ' · ' + noData + ' no data' : '');
}
function logicResultDetailHtml(block, st) {
    if (!block) return '<span class="msg">No logic blocks — author one in the Editor.</span>';
    const index = logicResultDefinitionIndex(block);
    const kind = logicKindOf(block);
    const word = st ? String(get(st, 'state') || 'unknown') : '';
    const reason = st && get(st, 'reason') ? String(get(st, 'reason')) : '';
    const reasonLine = word === 'ready' ? 'all conditions met'
        : word === 'blocked' || word === 'unknown' ? (reason ? 'Blocked by: ' + reason : '')
            : reason;
    const parts = [`<div class="result-head">
            <span class="result-title">${esc(get(block, 'name') || '(unnamed)')}</span>
            <span class="pill" style="padding:1px 6px;font-size:var(--fs-micro)">${esc(logicKindLabel(get(block, 'kind')))}</span>
            <span>${logicStateChip(st)}</span>
        </div>`];
    const description = String(get(block, 'description') || '');
    if (description) parts.push(`<div class="hint" style="margin-bottom:8px">${esc(description)}</div>`);
    if (!st) {
        parts.push('<div class="hint">No evaluation yet — waiting for the bridge.</div>');
        return parts.join('');
    }
    if (reasonLine) parts.push(`<div class="result-reason ${word === 'blocked' || word === 'unknown' ? 'bad' : 'good'}">${esc(reasonLine)}</div>`);
    if (kind === 'sequence') {
        parts.push('<div class="fp-k" style="margin-top:4px">Steps</div>');
        parts.push(logicResultStepCards(block, st, index));
    } else {
        parts.push('<div class="fp-k" style="margin-top:4px">Network</div>');
        parts.push(logicResultElementRows(get(st, 'elements') || [], index) || '<span class="msg">No evaluated elements.</span>');
    }
    return parts.join('');
}
function renderLogicResult() {
    const blocks = state.logic || [];
    const summary = el('logicResultSummary');
    if (summary) summary.textContent = logicResultSummaryText();
    if (!blocks.length) {
        state.logicResultId = '';
        el('logicResultList').innerHTML = '<span class="msg">No logic blocks yet — author one in the Editor.</span>';
        el('logicResultDetail').innerHTML = logicResultDetailHtml(null, null);
        return;
    }
    if (!blocks.some(block => String(get(block, 'id') || '') === state.logicResultId)) {
        // The editor's block is the natural landing point; otherwise the first one.
        const editor = blocks.find(block => String(get(block, 'id') || '') === state.logicSelectedId);
        state.logicResultId = String(get(editor || blocks[0], 'id') || '');
    }
    // The list follows the phone: a heading per interlock group, ungrouped blocks in
    // authored order at the top.
    const anyGrouped = blocks.some(block => String(get(block, 'group') || '').trim().length > 0);
    const listed = anyGrouped
        ? blocks.slice().sort((a, b) => {
            const ga = String(get(a, 'group') || '').trim();
            const gb = String(get(b, 'group') || '').trim();
            return ga.localeCompare(gb) || (Number(get(a, 'order') || 0) - Number(get(b, 'order') || 0));
        })
        : blocks;
    let lastGroup = null;
    el('logicResultList').innerHTML = listed.map(block => {
        const group = String(get(block, 'group') || '').trim();
        const head = anyGrouped && group !== lastGroup ? `<div class="logic-group-head">${esc(group || 'Ungrouped')}</div>` : '';
        lastGroup = group;
        return head + logicResultBlockRow(block, String(get(block, 'id') || '') === state.logicResultId);
    }).join('');
    const selected = blocks.find(block => String(get(block, 'id') || '') === state.logicResultId) || blocks[0];
    el('logicResultDetail').innerHTML = logicResultDetailHtml(selected, logicResultState(selected));
}
function selectLogicResultBlock(blockId) {
    state.logicResultId = String(blockId || '');
    renderLogicResult();
}
function onLogicResultClick(event) {
    const button = event.target.closest('button[data-result-block-id]');
    if (button) selectLogicResultBlock(button.dataset.resultBlockId || '');
}
function renumberLogicSteps() {
    const steps = document.querySelectorAll('#logicSteps .logic-step');
    steps.forEach((stepEl, index) => {
        const label = stepEl.querySelector('.logic-step-head .msg');
        if (label) label.textContent = 'STEP ' + (index + 1);
        const up = stepEl.querySelector('[data-action="logic-step-up"]');
        const down = stepEl.querySelector('[data-action="logic-step-down"]');
        if (up) up.disabled = index === 0;
        if (down) down.disabled = index === steps.length - 1;
    });
}
function onLogicBlockListClick(event) {
    const remove = event.target.closest('button[data-action="logic-delete-block"]');
    if (remove) {
        deleteLogicBlock(remove.dataset.blockId || '').catch(e => el('logicEditorMsg').textContent = '✗ ' + e.message);
        return;
    }
    const button = event.target.closest('button[data-action="logic-select"]');
    if (!button) return;
    selectLogicBlock(button.dataset.blockId || '');
}
function onLogicEditorClick(event) {
    const button = event.target.closest('button[data-action]');
    if (!button) return;
    const action = button.dataset.action;
    if (action === 'logic-element-indent' || action === 'logic-element-outdent') {
        const row = button.closest('.logic-element-row');
        if (!row) return;
        const indent = Number(row.dataset.indent || 0);
        const next = action === 'logic-element-indent' ? Math.min(indent + 1, 8) : Math.max(indent - 1, 0);
        row.dataset.indent = String(next);
        row.style.paddingLeft = (8 + next * 18) + 'px';
        return;
    }
    if (action === 'logic-element-remove') {
        const row = button.closest('.logic-element-row');
        if (row) row.remove();
        return;
    }
    if (action === 'logic-remove-condition') {
        const row = button.closest('.logic-cond-row');
        if (row) row.remove();
    } else if (action === 'logic-remove-action') {
        const row = button.closest('.logic-action-row');
        if (row) row.remove();
    } else if (action === 'logic-add-step-condition') {
        const stepEl = button.closest('.logic-step');
        const container = stepEl ? stepEl.querySelector('.logic-step-conditions') : null;
        if (!container) return;
        const empty = container.querySelector('.msg');
        if (empty) empty.remove();
        container.insertAdjacentHTML('beforeend', logicConditionRow({ id: logicNewId(), op: 'on', severity: 'block' }, stepEl.dataset.stepId));
    } else if (action === 'logic-step-remove') {
        const stepEl = button.closest('.logic-step');
        if (stepEl) { stepEl.remove(); renumberLogicSteps(); }
    } else if (action === 'logic-step-up' || action === 'logic-step-down') {
        const stepEl = button.closest('.logic-step');
        if (!stepEl) return;
        const sibling = action === 'logic-step-up' ? stepEl.previousElementSibling : stepEl.nextElementSibling;
        if (sibling) {
            if (action === 'logic-step-up') stepEl.parentNode.insertBefore(stepEl, sibling);
            else stepEl.parentNode.insertBefore(sibling, stepEl);
            renumberLogicSteps();
        }
    }
}
function onLogicEditorChange(event) {
    const field = event.target.closest('[data-field]');
    if (!field) return;
    const elementRow = field.closest('.logic-element-row');
    if (elementRow) {
        if (field.dataset.field === 'kind') {
            // A new kind brings its own fields; what carried over (the label) is kept.
            logicElementLiveRow(elementRow);
        } else if (field.dataset.field === 'source') {
            const itemSelect = elementRow.querySelector('[data-field="item"]');
            if (itemSelect) itemSelect.innerHTML = logicTagOptions(field.value, '', false);
        } else if (field.dataset.field === 'op') {
            const valueInput = elementRow.querySelector('[data-field="value"]');
            if (valueInput) valueInput.style.display = logicOpNeedsValue(field.value) ? '' : 'none';
        }
        return;
    }
    const row = field.closest('.logic-cond-row');
    if (row) {
        if (field.dataset.field === 'op') {
            const valueInput = row.querySelector('[data-field="value"]');
            if (valueInput) valueInput.style.display = logicOpNeedsValue(field.value) ? '' : 'none';
        } else if (field.dataset.field === 'source') {
            const itemSelect = row.querySelector('[data-field="item"]');
            if (itemSelect) itemSelect.innerHTML = logicTagOptions(field.value, '', false);
        }
        return;
    }
    const stepEl = field.closest('.logic-step');
    if (stepEl && field.dataset.field === 'completionSource') {
        const itemSelect = stepEl.querySelector('[data-field="completionItem"]');
        if (itemSelect) itemSelect.innerHTML = logicTagOptions(field.value, '', false);
    }
}
async function saveLogicBlock() {
    if (state.logicOffline) throw new Error('The bridge is unreachable — editing is disabled.');
    const block = collectLogicDraft();
    el('logicEditorMsg').textContent = 'Saving…';
    const r = await fetch('/api/logic/blocks', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ block })
    });
    const p = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    state.logicDraft = logicDeepCopy(p.block || block);
    state.logicSelectedId = String(get(p.block || block, 'id') || '');
    el('logicEditorMsg').textContent = '✓ Saved.';
    await loadLogic();
    renderLogicEditor();
    await loadLogicState();
    renderLogicLive();
}
async function deleteLogicBlock(blockId) {
    if (state.logicOffline) throw new Error('The bridge is unreachable — editing is disabled.');
    const id = blockId || state.logicSelectedId || String(get(state.logicDraft || {}, 'id') || '');
    if (!id) { el('logicEditorMsg').textContent = 'Nothing saved to delete.'; return; }
    const found = (state.logic || []).find(b => String(get(b, 'id') || '') === id);
    const name = found ? (get(found, 'name') || get(found, 'id')) : 'this block';
    if (!confirm('Delete "' + name + '"? Anything not saved is lost.')) return;
    const r = await fetch('/api/logic/blocks/' + encodeURIComponent(id), { method: 'DELETE' });
    const p = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    if (state.logicSelectedId === id) {
        state.logicSelectedId = '';
        state.logicDraft = null;
    }
    await loadLogic();
    renderLogicView();
    el('logicEditorMsg').textContent = '✓ Block deleted.';
}

async function loadTags() {
    try {
        const r = await fetch('/api/hmi/tags', { cache: 'no-store' });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const p = await r.json();
        state.tags = p.tags || [];
        cacheWrite(TAGS_KEY, { tags: state.tags });
        setBridgeOnline(true);
    } catch (e) {
        const cached = cacheRead(TAGS_KEY);
        state.tags = cached ? (cached.tags || []) : [];
        setBridgeOnline(false, 'Could not reach the bridge — showing the last known data. Editing is disabled until it is back.');
    }
}

async function loadLogic() {
    try {
        const r = await fetch('/api/logic', { cache: 'no-store' });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const p = await r.json();
        state.logic = p.blocks || [];
        cacheWrite(DEFS_KEY, { blocks: state.logic });
        setBridgeOnline(true);
    } catch (e) {
        const cached = cacheRead(DEFS_KEY);
        state.logic = cached ? (cached.blocks || []) : [];
        setBridgeOnline(false, 'Could not reach the bridge — showing the last known blocks. Editing is disabled until it is back.');
    }
    renderLogicView();
    if (state.activeView === 'result') renderLogicResult();
}

async function loadLogicState() {
    try {
        const r = await fetch('/api/logic/state', { cache: 'no-store' });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const p = await r.json();
        state.logicStateById = {};
        (p.blocks || []).forEach(block => { state.logicStateById[String(get(block, 'id') || '')] = block; });
        cacheWrite(STATE_KEY, { blocks: p.blocks || [] });
        setBridgeOnline(true);
    } catch (e) {
        const cached = cacheRead(STATE_KEY);
        state.logicStateById = {};
        ((cached && cached.blocks) || []).forEach(block => { state.logicStateById[String(get(block, 'id') || '')] = block; });
        setBridgeOnline(false, 'Could not reach the bridge — showing the last known state. Editing is disabled until it is back.');
    }
    renderLogicLive();
    if (state.activeView === 'result') renderLogicResult();
}

async function initLogicApp() {
    initTheme();
    defaultLogicMessage = el('logicMessage') ? el('logicMessage').textContent : '';
    // The hash deep-links the view (#/result); the editor stays the default.
    showView(String(location.hash || '').replace(/^#\/?/, '') === 'result' ? 'result' : 'editor', false);
    el('tabLogicEditor').addEventListener('click', () => showView('editor', true));
    el('tabLogicResult').addEventListener('click', () => showView('result', true));
    el('viewTabs').addEventListener('keydown', onViewTabKey);
    el('logicResultList').addEventListener('click', onLogicResultClick);
            el('btnLogicBlocks').addEventListener('click', () => openLogicBlocks());
        el('btnLogicBlocksClose').addEventListener('click', () => closeLogicBlocks());
        el('logicBlocksDialog').addEventListener('click', event => { if (event.target === el('logicBlocksDialog')) closeLogicBlocks(); });
        el('btnLogicAdd').addEventListener('click', () => addLogicBlock());
        el('btnLogicSave').addEventListener('click', () => saveLogicBlock().catch(e => el('logicEditorMsg').textContent = '✗ ' + e.message));
        el('btnLogicAddElement').addEventListener('click', () => {
            const container = el('logicElements');
            const empty = container.querySelector('.msg');
            if (empty) empty.remove();
            // A new row sits at the root; indent it under another row to make it an input.
            container.insertAdjacentHTML('beforeend', logicElementRow({ id: logicNewId(), kind: 'contact', op: 'on', severity: 'block', inputs: [] }, 0));
        });
        el('btnLogicToNetwork').addEventListener('click', () => {
            const draft = state.logicDraft || newLogicDraft();
            const elements = logicExpandConditions(collectCurrentConditions());
            if (!elements.length) {
                el('logicEditorMsg').textContent = 'Nothing to convert — add a condition first.';
                return;
            }

            draft.elements = elements;
            state.logicDraft = draft;
            renderLogicEditor();
            el('logicEditorMsg').textContent = '✓ Conditions rewritten as a network — Save Block to keep it.';
        });
        el('btnLogicToConditions').addEventListener('click', () => {
            const draft = state.logicDraft || newLogicDraft();
            if (!confirm('Drop the network and go back to the flat condition list? Anything not saved is lost.')) {
                return;
            }

            draft.elements = [];
            if (!(draft.conditions || []).length) {
                draft.conditions = [{ id: logicNewId(), text: '', sourceId: '', itemId: '', op: 'on', value: null, group: '', holdMs: 0, nextStepText: null, severity: 'block' }];
            }

            state.logicDraft = draft;
            renderLogicEditor();
            el('logicEditorMsg').textContent = '✓ Network dropped — Save Block to keep the flat list.';
        });
        el('btnLogicAddCondition').addEventListener('click', () => {
            const container = el('logicConditions');
            const empty = container.querySelector('.msg');
            if (empty) empty.remove();
            container.insertAdjacentHTML('beforeend', logicConditionRow({ id: logicNewId(), op: 'on', severity: 'block' }, null));
        });
        el('btnLogicAddStep').addEventListener('click', () => {
            const container = el('logicSteps');
            const empty = container.querySelector('.msg');
            if (empty) empty.remove();
            const count = container.querySelectorAll('.logic-step').length;
            container.insertAdjacentHTML('beforeend', logicStepCard({ id: logicNewId(), name: 'Step ' + (count + 1), conditions: [{ id: logicNewId(), op: 'on', severity: 'block' }] }, count, count + 1));
            renumberLogicSteps();
        });
        el('btnLogicAddAction').addEventListener('click', () => {
            const container = el('logicActions');
            const empty = container.querySelector('.msg');
            if (empty) empty.remove();
            container.insertAdjacentHTML('beforeend', logicActionRow({ label: '', sourceId: '', itemId: '', value: '', confirm: true }));
        });
        el('lgKind').addEventListener('change', () => renderLogicKindUi());
        el('logicBlockList').addEventListener('click', onLogicBlockListClick);
        el('logicEditor').addEventListener('click', onLogicEditorClick);
        el('logicEditor').addEventListener('change', onLogicEditorChange);
        el('btnGuide').addEventListener('click', () => openGuide());
        el('btnGuideClose').addEventListener('click', () => closeGuide());
        el('guideDialog').addEventListener('click', event => { if (event.target === el('guideDialog')) closeGuide(); });
        el('btnGuideTab').addEventListener('click', () => showGuideTab('guide'));
        el('btnNotesTab').addEventListener('click', () => showGuideTab('notes'));
        el('guideToc').addEventListener('click', event => {
            const topic = event.target.closest('button[data-guide-topic]');
            if (topic) showGuideTopic(Number(topic.dataset.guideTopic));
        });

    await loadVersion();
    await loadTags();
    await loadLogic();
    await loadLogicState();
    await refreshBridgeStatus();
    // The live chips repaint once a second; a hidden tab does not poll.
    setInterval(() => { if (!document.hidden) loadLogicState(); }, 1000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) loadLogicState(); });
}

document.addEventListener('DOMContentLoaded', () => {
    initLogicApp().catch(e => console.error(e));
});

</script>
""";

    public static string FullHtml => Html + Script;
}
