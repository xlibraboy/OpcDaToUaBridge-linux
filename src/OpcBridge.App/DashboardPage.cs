namespace OpcBridge.App;

// Test-asserted DOM/JS contract (see tests/OpcBridge.LoadTest/DashboardPageTests.cs, HelpContentTests.cs).
// Do NOT rename without updating tests:
//   data-tab="influx", id="view-influx", id="fpInfluxEnabled", id="influxUrl", id="influxWritten"
//   function loadInfluxConfig/loadInfluxStatus/saveInflux/connectInflux/disconnectInflux
//   /api/influx/config, influxEnabled: el('fpInfluxEnabled').checked, if (name === 'influx')
//   id="pApps", text "Apps", "pApps" in script, "detectedCount" in script
//   text "Interlinks", id="btnClearLinkSelection"
//   text "Clear Selection", text "Delete Saved Link", function clearInterlinkDraftSelection
//   state.interlinkDraft.consumer = null, state.interlinkDraft.provider = null
//   function renderInterlinkPickers(, state.interlinkDraft, data-action="pick-interlink-consumer", data-action="pick-interlink-provider"
//   data-tab="opc-da", id="view-opc-da", data-route="connectivity/opc-da", text "OPC DA"
//   Sources is a sidebar group label only (not a page); legacy connectivity/sources → opc-da
//   data-tab="drivers", id="view-drivers", data-route="connectivity/drivers", id="wzDrv" (driver wizard)
//   drvA3nSourceId/drvA3nName/drvA3nPort/drvA3nBaud/drvA3nDataBits/drvA3nParity/drvA3nStopBits/
//   drvA3nStation/drvA3nPc/drvA3nTimeout/drvA3nRetry/drvA3nRate/drvA3nMaxTags
//   ROUTE_TO_TAB 'connectivity/drivers': 'drivers', renderDrivers(/saveDriverSource(/testDriverConnection(
//   sourceType: 'MelsecA3n' save payload, /api/drivers/melsec-a3n/test-connection
//   data-tab="mx-component", id="view-mx-component", data-route="connectivity/mx-component"
//   renderMx(/saveMxSource(/testMxConnection(/mxFormBody(, /api/drivers/mx-component/test-connection
//   MX sources are separate from serial drivers: isDriverSource excludes MxComponent
//   data-tab="opc-ua", id="view-opc-ua", data-route="connectivity/opc-ua", text "OPC UA"
//   data-tab="connection", id="view-connection", id="sourcesStatusList", data-route="connectivity/sources", text "Sources"
//   id="uaCfgEndpointUrl", id="uaCfgSourceId", function saveUaSource/testUaConnection
//   data-tab="ua-subs", id="view-ua-subs", data-route="connectivity/ua-subs", text "UA Subs"
//   per-source collapsible cards in uaSubsContainer, uaSubModal add/edit, id="subsMsg"
//   function loadUaSubs(/renderUaSubsForSource(/openUaSubAdd(/openUaSubEdit(/deleteUaSub(/uaSubModalSave(, /api/ua/subscriptions[/remove]
//   faceplate: id="fpSubscription"/"fpSubscriptionField", function fpSubscriptionOptions(/updateFpRateEnabled(
//   id="mapTypeTabs", data-map-type="opc-da|opc-ua|drivers", function setMapType(/opcDaSources(/mapTypeSources(
//   tags/maps/opc-da, tags/maps/opc-ua, tags/maps/drivers
internal static class DashboardPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>OPC Bridge</title>
    <style>
        :root {
            color-scheme: dark;
            --bg: #0a0e14;
            --panel: #11161f;
            --panel2: #161c27;
            --border: #232b38;
            --border2: #2e3848;
            --text: #d8e0ea;
            --muted: #6b7689;
            --muted-strong: #93a0b4;
            --good: #34d399;
            --bad: #f87171;
            --warn: #fbbf24;
            --accent: #38bdf8;
            font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { background: var(--bg); color: var(--text); font-size: 13px; display: flex; flex-direction: column; height: 100vh; overflow: hidden; }
        .mono { font-family: 'Consolas', 'SF Mono', monospace; }
        .topbar { display: flex; align-items: center; gap: 14px; padding: 0 18px; height: 46px; background: var(--panel); border-bottom: 1px solid var(--border2); }
        .brand { display: flex; align-items: center; gap: 9px; font-weight: 600; font-size: 14px; white-space: nowrap; }
        .dot { width: 9px; height: 9px; border-radius: 50%; background: var(--good); }
        .ver { font-size: 10px; font-weight: 400; color: var(--muted); background: var(--panel2); border: 1px solid var(--border2); border-radius: 3px; padding: 1px 6px; margin-left: 4px; }
        .dot.off { background: var(--bad); }
        .pills { display: flex; gap: 7px; margin-left: 8px; flex-wrap: wrap; }
        .pill { display: flex; align-items: center; gap: 6px; background: var(--panel2); border: 1px solid var(--border); border-radius: 5px; padding: 3px 9px; font-size: 12px; white-space: nowrap; }
        .pill b { font-weight: 600; }
        .pill .k { color: var(--muted); text-transform: uppercase; font-size: 10px; letter-spacing: .05em; }
        .topbar .clock { margin-left: auto; color: var(--muted); font-size: 11px; white-space: nowrap; }
.app-shell { display: flex; flex: 1; min-height: 0; overflow: hidden; }
.tabbar { display: flex; flex-direction: column; background: var(--panel); border-right: 1px solid var(--border2); padding: 10px 0; width: 200px; flex-shrink: 0; overflow-y: auto; }
.tabbtn { background: none; border: none; color: var(--muted); padding: 11px 16px; font-size: 13px; font-weight: 500; cursor: pointer; border-left: 3px solid transparent; display: flex; align-items: center; gap: 8px; text-align: left; }
.tabbtn:hover { color: var(--text); background: var(--panel2); }
.tabbtn.active { color: var(--accent); border-left-color: var(--accent); background: var(--panel2); }
.nav-group { padding: 10px 0 8px; border-bottom: 1px solid var(--border); }
.nav-group:last-child { border-bottom: none; }
.nav-group-h { display: flex; align-items: center; gap: 7px; padding: 2px 16px 8px; font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: .09em; color: var(--muted-strong); transition: color .15s ease; }
.nav-group-h .nav-ico { width: 13px; height: 13px; flex-shrink: 0; opacity: .95; stroke: currentColor; fill: none; stroke-width: 1.7; stroke-linecap: round; stroke-linejoin: round; }
.nav-group:has(.tabbtn.active) .nav-group-h { color: var(--accent); }
.nav-group .tabbtn { position: relative; padding-top: 8px; padding-bottom: 8px; padding-left: 44px; }
.nav-group .tabbtn::before { content: ''; position: absolute; left: 24px; top: 0; bottom: 0; width: 2px; background: var(--border); }
.nav-group .tabbtn:hover::before { background: var(--border2); }
.nav-group .tabbtn.active::before { background: var(--accent); opacity: .5; }
.nav-group .tabbtn:last-child:not(.active)::before { bottom: auto; height: 55%; }
.content { flex: 1; min-width: 0; overflow: auto; }
.view { display: none; padding: 16px 18px; }
.view.active { display: block; }
@media (max-width: 600px) { .app-shell { flex-direction: column; } .tabbar { flex-direction: row; width: 100%; border-right: none; border-bottom: 1px solid var(--border2); padding: 0 8px; overflow-x: auto; } .tabbtn { border-left: none; border-bottom: 3px solid transparent; padding: 10px 14px; } .tabbtn.active { border-left: none; border-bottom-color: var(--accent); } .nav-group { border-bottom: none; } .nav-group-h { display: none; } .nav-group .tabbtn { padding: 10px 14px; } .nav-group .tabbtn::before { display: none; } }
        .grid2 { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
        @media (max-width: 900px) { .grid2 { grid-template-columns: 1fr; } }
        .box { background: var(--panel); border: 1px solid var(--border); border-radius: 7px; overflow: hidden; }
        .box-h { padding: 9px 14px; background: var(--panel2); border-bottom: 1px solid var(--border); font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: .07em; color: var(--muted); display: flex; align-items: center; gap: 8px; }
        .box-b { padding: 12px 14px; }
        .stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 10px; margin-bottom: 14px; }
        .mon-stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 10px; margin-bottom: 14px; }
        .mon-stat-group { background: var(--panel); border: 1px solid var(--border); border-radius: 7px; padding: 10px 12px; display: flex; flex-direction: column; gap: 8px; }
        .mon-stat-group-h { font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: .07em; color: var(--muted); padding-bottom: 6px; border-bottom: 1px solid var(--border); }
        .mon-stat-group .stat { padding: 4px 0; border: none; background: none; }
        .mon-stat-group .stat .v { font-size: 15px; }
        .mon-stat-group .stat .v .badge { font-size: 13px; }
        .stat { background: var(--panel); border: 1px solid var(--border); border-radius: 7px; padding: 11px 13px; }
        .alarm-bar { display: flex; align-items: center; gap: 10px; padding: 9px 14px; border-radius: 7px; margin-bottom: 14px; font-size: 12px; font-weight: 600; }
        .alarm-bar.ok { background: rgba(52,211,153,.1); border: 1px solid rgba(52,211,153,.3); color: var(--good); }
        .alarm-bar.warning { background: rgba(251,191,36,.1); border: 1px solid rgba(251,191,36,.3); color: var(--warn); }
        .alarm-bar.bad { background: rgba(248,113,113,.1); border: 1px solid rgba(248,113,113,.3); color: var(--bad); }
        .first-run-banner { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-radius: 7px; margin-bottom: 12px; font-size: 12px; background: rgba(56,189,248,.08); border: 1px solid rgba(56,189,248,.3); color: var(--text); }
        .session-warn-banner { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-radius: 7px; margin-bottom: 12px; font-size: 12px; font-weight: 600; background: rgba(245,158,11,.12); border: 1px solid rgba(245,158,11,.45); color: var(--text); }
        .first-run-banner button { margin-left: auto; }
        .port-banner { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-radius: 7px; margin-bottom: 12px; font-size: 12px; font-weight: 600; background: rgba(251,191,36,.1); border: 1px solid rgba(251,191,36,.3); color: var(--warn); }
        .port-banner button { margin-left: auto; }
        .session-warn-banner button { margin-left: auto; }
        .stat .k { color: var(--muted); font-size: 10px; text-transform: uppercase; letter-spacing: .06em; }
        .stat .v { margin-top: 6px; font-size: 16px; font-weight: 700; line-height: 1.1; }
        .stat .s { margin-top: 4px; color: var(--muted); font-size: 11px; }
        .mini-meter { margin-top: 7px; }
        .mini-meter-track { height: 6px; border-radius: 999px; background: var(--panel2); border: 1px solid var(--border); overflow: hidden; }
        .mini-meter-fill { height: 100%; width: 0%; background: var(--good); transition: width .2s ease, background-color .2s ease; }
        .mini-meter-fill.warn { background: var(--warn); }
        .mini-meter-fill.bad { background: var(--bad); }
        .badge { display: inline-flex; align-items: center; gap: 5px; padding: 1px 8px; border-radius: 10px; font-size: 12px; font-weight: 600; }
        .badge::before { content:''; width:6px; height:6px; border-radius:50%; background:currentColor; }
        .badge.good { color: var(--good); background: rgba(52,211,153,.12); }
        .badge.bad { color: var(--bad); background: rgba(248,113,113,.12); }
        .badge.warn { color: var(--warn); background: rgba(251,191,36,.12); }
        .badge.partial { color: var(--accent); background: rgba(56,189,248,.12); }
        table { width: 100%; border-collapse: collapse; }
        .values-wrap { overflow-x: auto; }
        .values-table { table-layout: fixed; }
        .values-table th { padding: 7px 10px; font-size: 10px; }
        .values-table td { padding: 7px 10px; font-size: 12px; line-height: 1.25; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; vertical-align: middle; }
        .values-table code, .values-table .mono { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 12px; }
        .values-table .quality { display: inline-flex; align-items: center; gap: 6px; }
        .values-table .timestamp { color: var(--muted); font-size: 11px; }
        .field { display: flex; align-items: center; gap: 8px; margin-bottom: 10px; flex-wrap: wrap; }
        .field:last-child { margin-bottom: 0; }
        label.fl { color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: .05em; width: 86px; flex-shrink: 0; }
        select, input[type=text], input[type=password] { background: var(--bg); color: var(--text); border: 1px solid var(--border2); border-radius: 5px; padding: 6px 9px; font-size: 13px; }
        input[type=text], input[type=password], select { min-width: 140px; }
        input:disabled, select:disabled { opacity: .72; cursor: not-allowed; }
        .btn { display: inline-flex; align-items: center; gap: 6px; background: var(--accent); color: #07121a; border: none; border-radius: 5px; padding: 6px 13px; font-size: 12px; font-weight: 600; cursor: pointer; white-space: nowrap; }
        .btn.ghost { background: var(--panel2); color: var(--text); border: 1px solid var(--border2); }
        .hint, .msg { font-size: 12px; color: var(--muted); }
        .list { display: flex; flex-direction: column; gap: 4px; max-height: 380px; overflow-y: auto; }
        .breadcrumb { display: flex; align-items: center; gap: 4px; flex-wrap: wrap; padding: 6px 10px; background: var(--bg); border: 1px solid var(--border2); border-radius: 5px; font-size: 12px; min-height: 32px; }
        .breadcrumb a { color: var(--accent); cursor: pointer; text-decoration: none; }
        .breadcrumb a:hover { text-decoration: underline; }
        .breadcrumb .sep { color: var(--muted); }
        .breadcrumb .current { color: var(--text); font-weight: 600; }
        .tag-browser-toolbar { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 8px; align-items: center; }
        .tag-browser-toolbar .msg { flex: 1; }
        .li .icon { font-size: 14px; flex-shrink: 0; width: 18px; text-align: center; }
        .li .icon.folder { color: var(--warn); }
        .li .icon.tag { color: var(--accent); }
        .li .icon.mapped { color: var(--good); }
        .li .li-actions { margin-left: auto; display: flex; gap: 6px; align-items: center; }
        .li .mapped-badge { font-size: 10px; color: var(--good); background: rgba(52,211,153,.12); padding: 1px 7px; border-radius: 10px; font-weight: 600; }
        .add-mapping-box { background: var(--bg); border: 1px solid var(--border2); border-radius: 5px; padding: 10px 12px; margin-bottom: 10px; }
        .add-mapping-box .field { margin-bottom: 8px; }
        .add-mapping-box .field:last-child { margin-bottom: 0; }
        .li { display: flex; align-items: center; gap: 10px; padding: 8px 10px; border-radius: 5px; border: 1px solid var(--border); background: var(--panel2); }
        .li .n { font-size: 13px; font-weight: 600; }
        .li .p { font-size: 11px; color: var(--muted); font-family: 'Consolas', monospace; }
        .li .li-desc { color: var(--muted); font-size: 13px; cursor: help; flex-shrink: 0; }
        .li .li-desc:hover { color: var(--accent); }
        .li.clickable { cursor: pointer; }
        .li.clickable:hover { border-color: var(--accent); }
        .li .li-badge { margin-left: auto; display: flex; align-items: center; gap: 6px; flex-wrap: nowrap; overflow: hidden; min-width: 0; }
        .li .li-badge-clip { display: flex; align-items: center; gap: 6px; overflow: hidden; min-width: 0; mask-image: linear-gradient(to right, black calc(100% - 14px), transparent); -webkit-mask-image: linear-gradient(to right, black calc(100% - 14px), transparent); }
        .li .li-badge-status { flex-shrink: 0; margin-left: 2px; display: flex; align-items: center; }
        .modal-overlay { display: none; position: fixed; inset: 0; background: rgba(0,0,0,.55); z-index: 1000; justify-content: center; align-items: center; }
        .modal-overlay.open { display: flex; }
        .modal { background: var(--panel); border: 1px solid var(--border2); border-radius: 8px; width: min(560px, 92vw); max-height: 90vh; overflow-y: auto; box-shadow: 0 8px 32px rgba(0,0,0,.4); }
        .modal-h { display: flex; align-items: start; justify-content: space-between; gap: 12px; padding: 14px 16px; border-bottom: 1px solid var(--border); }
        .modal-h .n { font-size: 15px; font-weight: 700; }
        .modal-h .p { font-size: 11px; color: var(--muted); font-family: 'Consolas', monospace; margin-top: 4px; }
        .modal-close { background: none; border: none; color: var(--muted); font-size: 20px; cursor: pointer; padding: 0 4px; line-height: 1; }
        .modal-close:hover { color: var(--text); }
        .modal-b { padding: 16px; display: flex; flex-direction: column; gap: 14px; }
        .fp-subtabs { display: flex; gap: 0; border-bottom: 1px solid var(--border); margin-bottom: 12px; }
        .fp-subtab { background: none; border: none; border-bottom: 2px solid transparent; color: var(--muted); padding: 8px 14px; font-size: 12px; font-weight: 600; cursor: pointer; }
        .fp-subtab:hover { color: var(--text); }
        .fp-subtab.active { color: var(--accent); border-bottom-color: var(--accent); }
        .fp-tabpane { display: flex; flex-direction: column; gap: 10px; }
        .fp-tabpane .field { margin-bottom: 0; }
        .fp-body { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
        .mapping-toolbar { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 10px; align-items: center; }
        @media (max-width: 520px) { .fp-body { grid-template-columns: 1fr; } }
        .fp-panel { background: var(--bg); border: 1px solid var(--border2); border-radius: 6px; padding: 12px 13px; }
        .fp-k { color: var(--muted); font-size: 10px; text-transform: uppercase; letter-spacing: .05em; margin-bottom: 7px; }
        .fp-v { font-size: 22px; font-weight: 700; line-height: 1.1; word-break: break-word; }
        .fp-meta { margin-top: 10px; color: var(--muted); font-size: 11px; display: flex; flex-direction: column; gap: 5px; }
        .fp-input { width: 100%; min-width: 0; font-size: 16px; }
        .fp-hint { margin-top: 7px; color: var(--muted); font-size: 11px; }
        .modal-f { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; padding: 12px 16px; border-top: 1px solid var(--border); }
        .modal-f .field { margin-bottom: 0; flex: 1; min-width: 200px; }
        .modal-f .btn { margin-left: auto; }
        .modal-f .btn + .btn { margin-left: 0; }
        .modal.wizard { width: 480px; max-width: 94vw; }
        .wizard-steps { display: flex; gap: 6px; padding: 10px 14px; border-bottom: 1px solid var(--border); flex-wrap: wrap; }
        .wizard-step, .wzdrv-step { font-size: 11px; color: var(--muted); padding: 3px 8px; border-radius: 4px; }
        .wizard-step.active, .wzdrv-step.active { color: var(--accent); background: rgba(56,189,248,.12); }
        .wizard-step.done, .wzdrv-step.done { color: var(--good); }
        .wizard-body { padding: 14px; max-height: 60vh; overflow-y: auto; }
        .wizard-pane, .wzdrv-pane { display: none; }
        .wizard-pane.active, .wzdrv-pane.active { display: block; }
        .wizard-foot { display: flex; justify-content: flex-end; gap: 8px; padding: 10px 14px; border-top: 1px solid var(--border); }
        .wizard-summary { font-size: 12px; line-height: 1.6; }
        .wizard-summary b { color: var(--text); }
        .endpoint { background: var(--bg); border: 1px solid var(--border2); border-radius: 5px; padding: 7px 11px; font-family: 'Consolas', monospace; font-size: 12px; color: var(--accent); word-break: break-all; }
        .split { display: grid; grid-template-columns: 1.2fr 1fr; gap: 12px; }
        .toolbar { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 10px; }
        .warn { color: var(--warn); }
        .good { color: var(--good); }
        .bad { color: var(--bad); }
        .source-row { display: grid; grid-template-columns: 1fr auto; gap: 10px; align-items: center; }
        .log-panel { display: flex; flex-direction: column; gap: 10px; }
        .log-view { background: var(--bg); border: 1px solid var(--border2); border-radius: 6px; padding: 10px 12px; max-height: 520px; overflow: auto; font-family: 'Consolas', 'SF Mono', monospace; font-size: 12px; line-height: 1.45; }
        .log-entry { padding: 8px 0; border-bottom: 1px solid var(--border); }
        .log-entry:last-child { border-bottom: none; }
        .log-entry .meta { color: var(--muted); font-size: 11px; margin-bottom: 4px; }
        .rate-limit-table { width: 100%; border-collapse: collapse; margin: 8px 0; font-size: 12px; }
        .address-ranges-table { width: 100%; border-collapse: collapse; margin: 8px 0; font-size: 12px; }
        .address-ranges-table th { text-align: left; padding: 4px 8px; border-bottom: 1px solid var(--border); white-space: nowrap; }
        .address-ranges-table td { padding: 4px 8px; border-bottom: 1px solid var(--border); vertical-align: top; }
        .rate-limit-table th { text-align: left; padding: 5px 8px; border-bottom: 1px solid var(--border2); color: var(--muted); font-size: 10px; text-transform: uppercase; letter-spacing: .05em; }
        .rate-limit-table td { padding: 5px 8px; border-bottom: 1px solid var(--border); }
        .rate-limit-table td:first-child { font-weight: 600; white-space: nowrap; }
        .rate-limit-table td:nth-child(2) { text-align: center; white-space: nowrap; }

        /* Shared data table (DA Groups etc.) */
        .tbl { width: 100%; border-collapse: collapse; }
        .tbl th { text-align: left; padding: 5px 8px; border-bottom: 1px solid var(--border2); color: var(--muted); font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: .05em; white-space: nowrap; }
        .tbl td { padding: 6px 8px; border-bottom: 1px solid var(--border); font-size: 12px; vertical-align: middle; }
        .tbl tbody tr:hover { background: rgba(56,189,248,.04); }
        .tbl th.num, .tbl td.num { text-align: right; }
        .tbl tfoot td { border-bottom: none; border-top: 1px solid var(--border2); padding-top: 9px; }
        .tbl input[type=text], .tbl select { min-width: 0; height: 24px; padding: 2px 6px; font-size: 12px; }
        .tbl input[type=text] { width: 130px; }
        .tbl select { width: auto; }
        .tbl .btn { height: 24px; padding: 0 10px; font-size: 11px; }

        /* DA Groups source-card header */
        .dag-src-name { font-weight: 600; font-size: 12.5px; color: var(--text); text-transform: none; letter-spacing: 0; }
        .dag-src-meta { font-family: 'Consolas', monospace; text-transform: none; letter-spacing: 0; margin-left: 2px; }
        .dag-src-host { margin-left: auto; font-family: 'Consolas', monospace; text-transform: none; letter-spacing: 0; }

        /* DA Groups v3 — card grid */
        .dag-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(210px, 1fr)); gap: 8px; }
        .dag-card { background: var(--bg); border: 1px solid var(--border2); border-radius: 6px; padding: 9px 11px; display: flex; flex-direction: column; gap: 6px; transition: opacity .15s ease; }
        .dag-card.default { border-color: rgba(56,189,248,.45); }
        .dag-card .n { font-size: 12.5px; font-weight: 600; word-break: break-all; display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
        .dag-badges { display: flex; gap: 5px; flex-wrap: wrap; }
        .dag-badge { font-size: 10px; padding: 1px 7px; border-radius: 9px; background: var(--panel2); border: 1px solid var(--border2); color: var(--muted); white-space: nowrap; }
        .dag-badge.accent { color: var(--accent); border-color: rgba(56,189,248,.35); }
        .dag-meta { font-size: 11px; color: var(--muted); }
        .dag-actions { display: flex; gap: 6px; margin-top: auto; align-items: center; }
        .dag-actions .btn { height: 22px; padding: 0 9px; font-size: 11px; }

        .log-entry .message { white-space: pre-wrap; word-break: break-word; }
        .log-entry .exception { white-space: pre-wrap; word-break: break-word; margin-top: 6px; color: var(--bad); }
        .log-entry .meta .lvl { font-weight: 600; }
        .log-entry .meta .lvl.trace, .log-entry .meta .lvl.debug { color: var(--muted); }
        .log-entry .meta .lvl.information { color: var(--accent); }
        .log-entry .meta .lvl.warning { color: var(--warn); }
        .log-entry .meta .lvl.error, .log-entry .meta .lvl.critical { color: var(--bad); }
        .log-entry .message.error, .log-entry .message.critical { color: var(--bad); }
        .help-subtabs, .map-type-tabs { display: flex; gap: 2px; background: var(--panel); border: 1px solid var(--border); border-radius: 6px; padding: 4px; margin-bottom: 12px; }
        .help-subtab, .map-type-tab { flex: 1; background: none; border: none; color: var(--muted); padding: 8px 16px; font-size: 12px; font-weight: 600; cursor: pointer; border-radius: 4px; transition: all .15s ease; }
        .help-subtab:hover, .map-type-tab:hover { color: var(--text); background: var(--panel2); }
        .help-subtab.active, .map-type-tab.active { color: var(--text); background: var(--panel2); box-shadow: 0 1px 3px rgba(0,0,0,.2); }
        .help-subtab-content { display: none; }
        .help-subtab-content.active { display: block; }
        .help-accordion { display: flex; flex-direction: column; gap: 8px; }
        .help-section { background: var(--panel); border: 1px solid var(--border); border-radius: 7px; overflow: hidden; }
        .help-section > summary { padding: 10px 14px; font-size: 13px; font-weight: 600; cursor: pointer; display: flex; align-items: center; gap: 8px; user-select: none; list-style: none; }
        .help-section > summary::-webkit-details-marker { display: none; }
        .help-section > summary::before { content: '\25B6'; font-size: 10px; color: var(--muted); transition: transform .15s ease; }
        .help-section[open] > summary::before { transform: rotate(90deg); }
        .help-section > summary:hover { background: var(--panel2); }
        .help-section[open] > summary { border-bottom: 1px solid var(--border); background: var(--panel2); }
        .help-body { padding: 12px 14px; }
        .help-body ul, .help-body ol { padding-left: 18px; color: var(--muted); }
        .help-body li + li { margin-top: 6px; }
        .help-body h4 { font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: .05em; color: var(--muted); margin: 12px 0 6px; }
        .help-body h4:first-child { margin-top: 0; }
        .help-body code { background: var(--bg); padding: 1px 5px; border-radius: 3px; font-size: 12px; }
        .help-body pre { background: var(--bg); border: 1px solid var(--border2); border-radius: 6px; padding: 12px 14px; overflow-x: auto; margin: 10px 0; }
        .help-body pre code { background: none; padding: 0; font-size: 12px; line-height: 1.5; font-family: 'Consolas', 'SF Mono', monospace; white-space: pre; color: var(--text); }
        .help-body h1 { display: none; }
        .help-body h2 { font-size: 12px; font-weight: 600; text-transform: uppercase; letter-spacing: .05em; color: var(--muted); margin: 14px 0 6px; }
        .help-body h3 { font-size: 13px; margin: 14px 0 6px; }
        .help-body p { color: var(--muted); margin: 6px 0; }
        .help-body em { color: var(--muted); font-size: 11px; }
        .help-body table { width: 100%; border-collapse: collapse; margin: 8px 0; font-size: 12px; }
        .help-body th { text-align: left; padding: 5px 8px; border-bottom: 1px solid var(--border2); color: var(--muted); font-size: 10px; text-transform: uppercase; letter-spacing: .05em; }
        .help-body td { padding: 5px 8px; border-bottom: 1px solid var(--border); }
        .help-body td:first-child { font-weight: 600; white-space: nowrap; }
        .kv { display: grid; grid-template-columns: 140px 1fr; gap: 8px 12px; align-items: start; }
        .kv .k { color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: .05em; }
        .kv .v { word-break: break-word; }
        @media (max-width: 1100px) { .split { grid-template-columns: 1fr; } }
        .conn-layout { display: grid; grid-template-columns: 1.4fr 1fr; gap: 14px; align-items: start; }
        @media (max-width: 1000px) { .conn-layout { grid-template-columns: 1fr; } }
        .conn-section { padding: 10px 0; border-top: 1px solid var(--border); }
        .conn-section:first-of-type { border-top: none; padding-top: 4px; }
        .conn-section-h { font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: .07em; color: var(--muted); margin-bottom: 8px; display: flex; align-items: center; gap: 8px; }
        .conn-section-h .msg { font-size: 10px; text-transform: none; letter-spacing: 0; }
        .info { display: inline-flex; align-items: center; justify-content: center; width: 11px; height: 11px; border-radius: 50%; background: var(--panel2); border: 1px solid var(--border2); color: var(--muted); font-size: 8px; font-weight: 700; font-style: italic; cursor: help; margin-left: 3px; user-select: none; vertical-align: middle; }
        .info:hover { color: var(--accent); border-color: var(--accent); }
        .tip { position: fixed; z-index: 9999; background: var(--panel2); color: var(--text); border: 1px solid var(--border2); border-radius: 5px; padding: 7px 11px; font-size: 11px; font-weight: 400; line-height: 1.5; max-width: 280px; box-shadow: 0 6px 16px rgba(0,0,0,.4); pointer-events: none; opacity: 0; transition: opacity .1s ease; }
        .tip.show { opacity: 1; }

        /* Diagram Tab Styles */
        .diag-toolbar {
            display: flex;
            align-items: center;
            gap: 16px;
            padding: 12px 20px;
            border-bottom: 1px solid var(--border);
            background: var(--panel);
        }
        .diag-seg {
            position: relative;
            display: inline-flex;
            align-items: center;
            gap: 2px;
            padding: 3px;
            background: var(--panel2);
            border: 1px solid var(--border);
            border-radius: 8px;
        }
        .seg-pill {
            position: absolute;
            top: 3px;
            bottom: 3px;
            left: 0;
            width: 0;
            border-radius: 6px;
            background: var(--accent);
            background: linear-gradient(180deg, var(--accent), color-mix(in srgb, var(--accent) 75%, #000));
            box-shadow: 0 2px 10px rgba(0,0,0,.35);
            transition: transform .28s cubic-bezier(.22,.9,.34,1), width .28s cubic-bezier(.22,.9,.34,1);
            pointer-events: none;
        }
        .diag-tab {
            position: relative;
            z-index: 1;
            background: transparent;
            border: none;
            color: var(--muted);
            padding: 6px 14px;
            border-radius: 6px;
            cursor: pointer;
            font-size: 12px;
            font-weight: 600;
            transition: color 0.18s;
        }
        .diag-tab:hover {
            color: var(--text);
        }
        .diag-tab.active {
            color: var(--bg);
        }
        .diag-zoom {
            display: flex;
            align-items: center;
            gap: 6px;
            margin-left: 8px;
        }
        .diag-zoom-btn {
            background: var(--panel2);
            border: 1px solid var(--border);
            color: var(--text);
            min-width: 28px;
            height: 28px;
            padding: 0 8px;
            border-radius: 4px;
            cursor: pointer;
            font-size: 13px;
            font-weight: 600;
            line-height: 1;
        }
        .diag-zoom-btn:hover { background: var(--border); }
        .diag-zoom-btn:disabled { opacity: 0.4; cursor: default; }
        .diag-zoom-label {
            min-width: 44px;
            text-align: center;
            font-size: 11px;
            color: var(--muted);
            font-variant-numeric: tabular-nums;
        }
        .diag-legend {
            margin-left: auto;
            display: flex;
            gap: 16px;
            font-size: 11px;
            color: var(--muted);
        }
        .legend-chip {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            padding: 3px 10px;
            border-radius: 999px;
            background: var(--panel2);
            border: 1px solid var(--border);
        }
        .legend-chip .legend-dot.good { box-shadow: 0 0 6px var(--good); }
        .legend-chip .legend-dot.warn { box-shadow: 0 0 6px var(--warn); }
        .legend-chip .legend-dot.bad { box-shadow: 0 0 6px var(--bad); }
        .legend-dot {
            width: 8px;
            height: 8px;
            border-radius: 50%;
        }
        .legend-dot.good { background: var(--good); }
        .legend-dot.warn { background: var(--warn); }
        .legend-dot.bad { background: var(--bad); }
        .legend-dot.off { background: var(--muted); }
        .diag-canvas {
            flex: 1;
            overflow: auto;
            background: var(--bg);
            position: relative;
            cursor: grab;
            user-select: none;
        }
        .diag-canvas.panning { cursor: grabbing; }
        .diag-zoom-host {
            position: relative;
            transform-origin: 0 0;
        }
        #diagSvg {
            display: block;
            transform-origin: 0 0;
        }
        .diag-node {
            cursor: pointer;
            animation: diagNodeIn 0.45s cubic-bezier(.22,.9,.34,1) both;
            animation-delay: calc(var(--i, 0) * 30ms);
        }
        @keyframes diagNodeIn {
            from { opacity: 0; transform: translateY(14px); }
            to { opacity: 1; transform: translateY(0); }
        }
        .diag-node > rect {
            fill: url(#diagCardGrad);
            filter: url(#diagDrop);
        }
        .diag-node rect {
            transition: all 0.15s;
        }
        .diag-node:hover rect {
            stroke-width: 2;
        }
        .diag-node text {
            fill: var(--text);
            font-size: 11px;
            font-family: var(--font-mono);
            pointer-events: none;
        }
        .diag-edge {
            fill: none;
            stroke-width: 2;
            stroke-dasharray: 1;
            transition: stroke 0.3s;
            animation: diagEdgeDraw 0.55s ease-out both;
            animation-delay: calc(var(--i, 0) * 22ms + 180ms);
        }
        .diag-edge.bad {
            animation: diagEdgeDraw 0.55s ease-out both,
                       diagPulse 1.8s ease-in-out calc(var(--i, 0) * 22ms + 900ms) infinite;
        }
        @keyframes diagEdgeDraw {
            from { stroke-dashoffset: 1; }
            to { stroke-dashoffset: 0; }
        }
        @keyframes diagPulse {
            0%, 100% { opacity: 1; }
            50% { opacity: 0.35; }
        }
        .diag-edge.good { stroke: var(--good); }
        .diag-edge.warn { stroke: var(--warn); }
        .diag-edge.bad { stroke: var(--bad); }
        .diag-edge.off { stroke: var(--muted); opacity: 0.4; }
        .diag-flow {
            fill: none;
            stroke-width: 3;
            stroke-dasharray: 8 8;
            stroke-linecap: round;
            animation: flow 1s linear infinite;
        }
        .diag-flow.good { stroke: var(--good); }
        .diag-flow.warn { stroke: var(--warn); }
        .diag-flow.bad { stroke: var(--bad); }
        .diag-flow.off { stroke: var(--muted); opacity: 0.3; animation: none; }
        @keyframes flow {
            to { stroke-dashoffset: -16; }
        }
        .diag-tooltip {
            position: absolute;
            background: var(--panel2);
            border: 1px solid var(--border);
            border-radius: 4px;
            padding: 8px 12px;
            font-size: 11px;
            color: var(--text);
            pointer-events: none;
            opacity: 0;
            transition: opacity 0.15s;
            z-index: 1000;
            max-width: 300px;
        }
        .diag-tooltip.visible {
            opacity: 1;
        }
        .diag-tooltip-row {
            display: flex;
            justify-content: space-between;
            gap: 12px;
            margin: 2px 0;
        }
        .diag-tooltip-label {
            color: var(--muted);
        }
        .diag-tooltip-value {
            font-family: var(--font-mono);
            font-weight: 500;
        }
        @media (prefers-reduced-motion: reduce) {
            .diag-node, .diag-edge, .diag-flow, .seg-pill, .diag-tooltip {
                animation: none !important;
                transition: none !important;
            }
        }
    </style>
</head>
<body>
<div class="topbar">
    <div class="brand"><span class="dot" id="dot"></span>OPC Bridge <span class="ver" id="appVersion"></span></div>
    <div class="pills">
        <div class="pill"><span class="k">Bridge</span><span id="pBridge">&#8212;</span></div>
        <div class="pill"><span class="k">DA</span><span id="pDa">&#8212;</span></div>
        <div class="pill"><span class="k">UA</span><span id="pUa">&#8212;</span></div>
        <div class="pill"><span class="k">Tags</span><b id="pTags">0</b></div>
         <div class="pill"><span class="k">Sources</span><b id="pSources">0</b></div>
         <div class="pill"><span class="k">Apps</span><b id="pApps">1</b></div>
    </div>
    <div class="clock" id="clock">&#8212;</div>
</div>
<div class="app-shell">
<div class="tabbar">
  <div class="nav-group">
    <div class="nav-group-h"><svg class="nav-ico" viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="4" width="18" height="6" rx="1.5"/><rect x="3" y="14" width="18" height="6" rx="1.5"/><circle cx="7" cy="7" r="1" fill="currentColor" stroke="none"/><circle cx="7" cy="17" r="1" fill="currentColor" stroke="none"/></svg>Sources</div>
    <button class="tabbtn" data-tab="connection" data-route="connectivity/sources" onclick="navigate('connectivity/sources')">Sources</button>
    <button class="tabbtn" data-tab="opc-da" data-route="connectivity/opc-da" onclick="navigate('connectivity/opc-da')">OPC DA</button>
    <button class="tabbtn" data-tab="opc-da-groups" data-route="connectivity/opc-da-groups" onclick="navigate('connectivity/opc-da-groups')">DA Groups</button>
    <button class="tabbtn" data-tab="opc-ua" data-route="connectivity/opc-ua" onclick="navigate('connectivity/opc-ua')">OPC UA</button>
    <button class="tabbtn" data-tab="ua-subs" data-route="connectivity/ua-subs" onclick="navigate('connectivity/ua-subs')">UA Subs</button>
    <button class="tabbtn" data-tab="drivers" data-route="connectivity/drivers" onclick="navigate('connectivity/drivers')">Drivers</button>
    <button class="tabbtn" data-tab="mx-component" data-route="connectivity/mx-component" onclick="navigate('connectivity/mx-component')">MX Component</button>
  </div>
  <div class="nav-group">
    <div class="nav-group-h"><svg class="nav-ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z"/><circle cx="7" cy="7" r="1.5" fill="currentColor" stroke="none"/></svg>Tags</div>
    <button class="tabbtn" data-tab="tags" data-route="tags/maps" onclick="navigate('tags/maps')">Maps</button>
    <button class="tabbtn" data-tab="interlinks" data-route="tags/interlinks" onclick="navigate('tags/interlinks')">Interlinks</button>
  </div>
  <div class="nav-group">
    <div class="nav-group-h"><svg class="nav-ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12.55a11 11 0 0 1 14.08 0"/><path d="M1.42 9a16 16 0 0 1 21.16 0"/><path d="M8.53 16.11a6 6 0 0 1 6.95 0"/><circle cx="12" cy="20" r="1.2" fill="currentColor" stroke="none"/></svg>IoT</div>
    <button class="tabbtn" data-tab="mqtt" data-route="iot/mqtt" onclick="navigate('iot/mqtt')">MQTT</button>
    <button class="tabbtn" data-tab="iot-traffic" data-route="iot/traffic" onclick="navigate('iot/traffic')">Traffic</button>
  </div>
  <div class="nav-group">
    <div class="nav-group-h"><svg class="nav-ico" viewBox="0 0 24 24" aria-hidden="true"><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v6c0 1.66 3.58 3 8 3s8-1.34 8-3V5"/><path d="M4 11v6c0 1.66 3.58 3 8 3s8-1.34 8-3v-6"/></svg>Historian</div>
    <button class="tabbtn" data-tab="influx" data-route="historian/influx" onclick="navigate('historian/influx')">InfluxDB</button>
  </div>
  <div class="nav-group">
    <div class="nav-group-h"><svg class="nav-ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M22 12h-4l-3 9L9 3l-3 9H2"/></svg>Ops</div>
    <button class="tabbtn active" data-tab="monitor" data-route="ops/monitor" onclick="navigate('ops/monitor')">Monitor</button>
    <button class="tabbtn" data-tab="diagnostics" data-route="ops/diagnostics" onclick="navigate('ops/diagnostics')">Diagnostics</button>
    <button class="tabbtn" data-tab="values" data-route="ops/values" onclick="navigate('ops/values')">Live Values</button>
    <button class="tabbtn" data-tab="sessions" data-route="ops/sessions" onclick="navigate('ops/sessions')">Sessions</button>
    <button class="tabbtn" data-tab="logs" data-route="ops/logs" onclick="navigate('ops/logs')">Logs</button>
    <button class="tabbtn" data-tab="diagram" data-route="ops/diagram" onclick="navigate('ops/diagram')">Diagram</button>
  </div>
  <div class="nav-group">
    <div class="nav-group-h"><svg class="nav-ico" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9"/><path d="M9.1 9a3 3 0 0 1 5.8 1c0 2-3 2.5-3 4.5"/><circle cx="12" cy="17.2" r="0.9" fill="currentColor" stroke="none"/></svg>Help</div>
    <button class="tabbtn" data-tab="help" data-route="help/guide" onclick="navigate('help/guide')">Guide</button>
    <button class="tabbtn" data-tab="about" data-route="help/about" onclick="navigate('help/about')">About</button>
  </div>
</div>
<div class="content">
<div class="view active" id="view-monitor">
    <div class="port-banner" id="portBanner" style="display:none"></div>
    <div class="first-run-banner" id="bannerNoSources" style="display:none"></div>
    <div class="first-run-banner" id="bannerNoMappings" style="display:none"></div>
    <div class="alarm-bar" id="rateAlarmBar" style="display:none"></div>
    <div class="session-warn-banner" id="sessionBanner" style="display:none"></div>
    <div class="mon-stats">
        <div class="mon-stat-group">
            <div class="mon-stat-group-h">Bridge</div>
            <div class="stat"><div class="k">Runtime</div><div class="v" id="bridgeState">&#8212;</div><div class="s" id="lastError">No errors</div></div>
        </div>
        <div class="mon-stat-group">
            <div class="mon-stat-group-h">Ports <span class="info" data-tip="Listening ports for this bridge. When the default port is already in use by another application, the bridge auto-assigns the next free port and saves it to appsettings.json (Bridge:HttpPort / Bridge:OpcUaPort).">i</span></div>
            <div class="stat"><div class="k">HTTP</div><div class="v" id="httpPortVal">&#8212;</div><div class="s" id="httpPortNote">Dashboard + API</div></div>
            <div class="stat"><div class="k">OPC UA</div><div class="v" id="uaPortVal">&#8212;</div><div class="s" id="uaPortNote">UA server endpoint</div></div>
        </div>
        <div class="mon-stat-group">
            <div class="mon-stat-group-h">OPC DA</div>
            <div class="stat"><div class="k">Connection</div><div class="v" id="daState">&#8212;</div></div>
            <div class="stat"><div class="k">Last Read</div><div class="v" id="lastDaRead">&#8212;</div><div class="s" id="lastDaReadCount">0 values</div></div>
        </div>
        <div class="mon-stat-group">
            <div class="mon-stat-group-h">OPC UA</div>
            <div class="stat"><div class="k">Server</div><div class="v" id="uaState">&#8212;</div><div class="s" id="uaClients">0 clients</div></div>
            <div class="stat"><div class="k">Last Write</div><div class="v" id="lastUaWrite">&#8212;</div><div class="s" id="lastUaWriteCount">0 values</div></div>
        </div>
        <div class="mon-stat-group">
            <div class="mon-stat-group-h">Update Rate</div>
            <div class="stat"><div class="k">Default Rate</div><div class="v" id="updateRate">&#8212;</div><div class="s" id="mappingCount">0 tags</div></div>
            <div class="stat"><div class="k">Cycle Budget</div><div class="mini-meter" aria-hidden="true"><div class="mini-meter-track"><div class="mini-meter-fill" id="pollUtilizationFill"></div></div></div><div class="s" id="pollUtilizationText">—</div><div class="s" id="pollSaturation">—</div></div>
        </div>
        <div class="mon-stat-group">
            <div class="mon-stat-group-h">Resources <span class="info" data-tip="Native Windows process counters sampled every 5s. A steady or slowly growing count is normal; a steady upward trend signals a handle or COM object leak.">i</span></div>
            <div class="stat"><div class="k">Handles <span class="info" data-tip="Total OS handles (files, registry keys, threads, events, COM objects) held by the process via GetProcessHandleCount. Typical idle: 300-800; investigate if it grows unbounded over time.">i</span></div><div class="v" id="resHandles">&#8212;</div></div>
            <div class="stat"><div class="k">GDI / USER <span class="info" data-tip="GDI objects (pens, brushes, fonts, bitmaps) and USER objects (windows, menus, hooks) via GetGuiResources. Each has a per-process limit of 10,000; approaching it indicates a GDI/USER leak.">i</span></div><div class="v" id="resGdiUser">&#8212;</div></div>
            <div class="stat"><div class="k">Assessment</div><div class="v" id="resAssessment">&#8212;</div><div class="s" id="resAssessmentDetail">Awaiting data…</div></div>
        </div>
    </div>
    <div class="grid2" style="margin-bottom:14px">
        <div class="box">
            <div class="box-h">Source Status <span class="msg" id="sourceCountH" style="margin-left:auto"></span></div>
            <div class="box-b"><div class="list" id="sourceStatusList" style="max-height:300px"></div></div>
        </div>
        <div class="box">
            <div class="box-h">OPC UA Endpoint</div>
            <div class="box-b">
                <div class="endpoint" id="uaEndpoint">&#8212;</div>
                <div class="msg" style="margin-top:6px;color:var(--muted)">Server bind address (0.0.0.0 = all interfaces)</div>
                <div style="margin-top:10px"><div class="k" style="font-size:11px;text-transform:uppercase;letter-spacing:.05em;color:var(--muted)">Connect from client</div><div class="endpoint" id="uaConnectUrl" style="margin-top:3px">&#8212;</div></div>
                <div class="msg" style="margin-top:6px;color:var(--muted)">Use this URL in your OPC UA client to connect from another machine</div>
                <div class="msg" id="uaDiagnostics" style="margin-top:8px">0 nodes · no updates yet</div>
            </div>
        </div>
    </div>
    <div class="box" style="margin-top:14px">
        <div class="box-h">Bridge Fleet <span class="info" data-tip="Other OpcBridge instances discovered on the network (UDP/HTTP probe). 'Local' is the instance serving this dashboard.">i</span><span class="msg" id="fleetCount" style="margin-left:auto"></span></div>
        <div class="box-b"><div class="list" id="fleetList" style="max-height:220px"><span class="msg">No fleet data yet.</span></div></div>
    </div>
</div>
<div class="view" id="view-values">
    <div class="box">
        <div class="box-h">Live Values <span class="msg" id="valCount" style="margin-left:auto"></span><select id="liveValuesSource" title="Filter live values by source" style="margin-left:8px"><option value="">All sources</option></select><button class="btn ghost" id="toggleLiveValues" type="button">Disable Live Data</button></div>
        <div class="box-b" style="padding:0">
            <div class="values-wrap">
                <table class="values-table">
                    <colgroup><col style="width:11%"><col style="width:23%"><col style="width:20%"><col style="width:9%"><col style="width:9%"><col style="width:10%"><col style="width:18%"></colgroup>
                    <thead><tr><th>Source</th><th>Item ID</th><th>Value</th><th>Type</th><th>Rate</th><th>Quality</th><th>Timestamp</th></tr></thead>
                    <tbody id="values"><tr><td colspan="7" class="msg">Waiting for values&#8230;</td></tr></tbody>
                </table>
            </div>
        </div>
    </div>
</div>
<div class="view" id="view-diagnostics">
    <div class="box" style="margin-bottom:14px">
        <div class="box-h">Bridge Vitals <span class="info" data-tip="Process-level metrics shown nowhere else: uptime, aggregate values/sec through the bridge, duration of the most recent DA poll cycle, and the Windows session hosting the DA servers. Connection states, counters and last-error live on ops/monitor.">i</span><span class="msg" id="diagHealthUpdated" style="margin-left:auto"></span></div>
        <div class="box-b">
            <div class="stats">
                <div class="stat"><div class="k">Uptime</div><div class="v" id="diagUptime">&#8212;</div></div>
                <div class="stat"><div class="k">Values/sec</div><div class="v" id="diagValueRate">&#8212;</div><div class="s" id="diagUpdateRate"></div></div>
                <div class="stat"><div class="k">Last Poll</div><div class="v" id="diagPollDuration">&#8212;</div></div>
                <div class="stat"><div class="k">DCOM Session</div><div class="v" id="diagSessionId">&#8212;</div><div class="s" id="diagInteractive"></div></div>
            </div>
        </div>
    </div>
    <div class="grid2" style="margin-bottom:14px">
        <div class="box">
            <div class="box-h">MQTT <span class="msg" id="diagMqttBadge" style="margin-left:auto"></span></div>
            <div class="box-b">
                <div class="stats">
                    <div class="stat"><div class="k">Throughput</div><div class="v" id="diagMqttState">&#8212;</div><div class="s" id="diagMqttTotals"></div></div>
                    <div class="stat"><div class="k">Rates</div><div class="v" id="diagMqttRate">&#8212;</div></div>
                </div>
                <div class="hint" id="diagMqttError" style="display:none;margin-top:8px"></div>
            </div>
        </div>
        <div class="box">
            <div class="box-h">InfluxDB <span class="msg" id="diagInfluxBadge" style="margin-left:auto"></span></div>
            <div class="box-b">
                <div class="stats">
                    <div class="stat"><div class="k">Throughput</div><div class="v" id="diagInfluxState">&#8212;</div><div class="s" id="diagInfluxTotal"></div></div>
                    <div class="stat"><div class="k">Write Rate</div><div class="v" id="diagInfluxRate">&#8212;</div></div>
                </div>
                <div class="hint" id="diagInfluxError" style="display:none;margin-top:8px"></div>
            </div>
        </div>
    </div>
    <div class="grid2" style="margin-bottom:14px">
        <div class="box">
            <div class="box-h">Disconnected Tags <span class="info" data-tip="UA monitored items whose creation failed and are being retried automatically. Common causes: the item disappeared from the source, or the source connection dropped.">i</span><span class="msg" id="diagDiscCount" style="margin-left:auto"></span></div>
            <div class="box-b"><div class="list" id="diagDisconnected" style="max-height:220px"><span class="msg">Loading…</span></div></div>
        </div>
        <div class="box">
            <div class="box-h">Bad Quality Tags <span class="info" data-tip="Mapped tags currently delivering a non-good quality from their source. The count reflects all affected tags; the list shows up to 50.">i</span><span class="msg" id="diagBadCount" style="margin-left:auto"></span></div>
            <div class="box-b"><div class="list" id="diagBadQuality" style="max-height:220px"><span class="msg">Loading…</span></div></div>
        </div>
    </div>
    <div class="grid2">
        <div class="box">
            <div class="box-h">Write Queue <span class="info" data-tip="UA client writes are queued in a bounded channel (capacity 1024) and drained by per-source consumer tasks. Success rate shows confirmed DA writes.">i</span></div>
            <div class="box-b">
                <div class="stats">
                    <div class="stat"><div class="k">Current Depth</div><div class="v" id="diagWqDepth">&#8212;</div></div>
                    <div class="stat"><div class="k">Success Rate</div><div class="v" id="diagWqRate">&#8212;</div><div class="s" id="diagWqTotals">0 enqueued</div></div>
                </div>
            </div>
        </div>
        <div class="box">
            <div class="box-h">STA Thread Health <span class="info" data-tip="Each OPC DA source has a dedicated Single-Threaded Apartment (STA) thread. All COM calls for that source serialize through it. 'Queued' shows pending COM operations; 'Last action' shows the most recent COM call time.">i</span></div>
            <div class="box-b"><div class="list" id="diagStaThreads" style="max-height:280px"><span class="msg">Loading…</span></div></div>
        </div>
    </div>
</div>
<div class="view" id="view-sessions">
    <div class="box" style="margin-bottom:14px">
        <div class="box-h">Source Diagnostics <span class="info" data-tip="Health of every configured source — OPC DA, OPC UA, and driver sources (Melsec, S7, MX Component). Shows connection state, read latency, rate-group budget for polled sources, the last fault reason, and data freshness.">i</span><span class="msg" id="diagDaSummary" style="margin-left:auto"></span></div>
        <div class="box-b" id="diagDaSources"><span class="msg">Loading…</span></div>
    </div>
    <div class="box" style="margin-bottom:14px">
        <div class="box-h">Time Sync <span class="info" data-tip="OPC DA sources only: compares the DA server's clock to the bridge machine's clock. A large offset (>500ms) indicates the DA server or bridge needs NTP time sync. UA clients receive both SourceTimestamp (DA server time) and ServerTimestamp (bridge time) for each value. UA and driver sources have no DA clock and show —.">i</span></div>
        <div class="box-b"><div class="list" id="diagTimeSync"><span class="msg">Loading…</span></div></div>
    </div>
    <div class="grid2" style="margin-bottom:14px">
        <div class="box">
            <div class="box-h">UA Sessions <span class="msg" id="diagUaSessionCount" style="margin-left:auto"></span></div>
            <div class="box-b"><div class="list" id="diagUaSessions" style="max-height:300px"><span class="msg">Loading…</span></div></div>
        </div>
        <div class="box">
            <div class="box-h">UA Subscriptions <span class="msg" id="diagUaSubCount" style="margin-left:auto"></span></div>
            <div class="box-b"><div class="list" id="diagUaSubscriptions" style="max-height:300px"><span class="msg">Loading…</span></div></div>
        </div>
    </div>
    <div class="box">
        <div class="box-h">UA Bandwidth <span class="info" data-tip="Notifications/sec counts how many value changes were pushed to UA nodes. Estimated bandwidth = notifications/sec x ~80 bytes (typical UA notification encoding). The SDK does not expose actual wire bytes.">i</span></div>
        <div class="box-b">
            <div class="stats">
                <div class="stat"><div class="k">Notifications/sec</div><div class="v" id="diagNotifPerSec">&#8212;</div></div>
                <div class="stat"><div class="k">Est. Bandwidth</div><div class="v" id="diagBandwidth">&#8212;</div><div class="s" id="diagTotalNotif">0 total</div></div>
            </div>
        </div>
    </div>
</div>
<div class="view" id="view-connection">
    <div class="box">
        <div class="box-h">Sources <button class="btn" type="button" onclick="openAddSourceWizard()" style="margin-left:auto">+ Add Source</button></div>
        <div class="box-b">
            <div class="hint" id="sourcesStatusHint">Select a source to open its OPC DA or OPC UA configuration.</div>
            <div class="list" id="sourcesStatusList" style="max-height:none"></div>
        </div>
    </div>
</div>
<div class="view" id="view-opc-da">
    <div class="conn-layout">
        <div class="conn-main">
            <div class="box">
                <div class="box-h">OPC DA Configuration <button class="btn" type="button" onclick="openAddSourceWizard()" style="margin-left:auto">+ Add Source</button><span class="msg" id="cfgMessage" style="font-weight:400;text-transform:none;letter-spacing:0">Select a saved connection or click New.</span></div>
                <div class="box-b">
                    <div class="field"><label class="fl">Selected</label><select id="selectedSource"></select></div>
                    <div class="conn-section">
                        <div class="conn-section-h">Identity</div>
                        <div class="field"><label class="fl">Source ID <span class="info" data-tip="Unique key with no spaces. Used internally and in UA Node IDs (ns=2;s={sourceId}/...).">i</span></label><input id="cfgSourceId" type="text" placeholder="server-a" style="flex:1"></div>
                        <div class="field"><label class="fl">Name <span class="info" data-tip="Friendly label shown in lists and the Tags tab.">i</span></label><input id="cfgDisplayName" type="text" placeholder="Production Line A" style="flex:1"></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">Server Address</div>
                        <div class="field"><label class="fl">ProgID <span class="info" data-tip="Programmatic ID of the OPC DA server (e.g. Matrikon.OPC.Simulation.1). Pick from the Discover panel on the right.">i</span></label><input id="cfgProgId" type="text" placeholder="Matrikon.OPC.Simulation.1" style="flex:1"></div>
                        <div class="field"><label class="fl">Host <span class="info" data-tip="Machine where the OPC DA server runs. Use 'localhost' for this PC, or an IP/hostname for remote.">i</span></label><input id="cfgHost" type="text" placeholder="localhost" style="flex:1"></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">Detected Server <span class="info" data-tip="Identified on connect: OPC DA spec level (1.0/2.0/3.0, probed from the server-object marker interfaces) plus the server's own version and vendor string (IOPCServer.GetStatus).">i</span></div>
                        <div class="field"><label class="fl">Server</label><span class="msg" id="cfgServerInfo" style="font-weight:400;text-transform:none;letter-spacing:0">—</span></div>
                        <div class="field"><label class="fl">Read Mode <span class="info" data-tip="How values are delivered right now: async (subscription) = IOPCDataCallback push; sync (polling) = IOPCSyncIO.Read polling. Updated live; depends on the server exposing the callback connection point.">i</span></label><span class="msg" id="cfgReadMode" style="font-weight:400;text-transform:none;letter-spacing:0">—</span></div>
                        <div class="field"><label class="fl">Write Mode <span class="info" data-tip="How writes are sent right now: DA follows the source's I/O mode — async (IOPCAsyncIO2.Write, confirmed via OnWriteComplete) when the push path is live, otherwise sync (IOPCSyncIO.Write). UA uses the Write service.">i</span></label><span class="msg" id="cfgWriteMode" style="font-weight:400;text-transform:none;letter-spacing:0">—</span></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">Credentials <span class="info" data-tip="Only required for remote DCOM with specific user accounts, or to access OPC DA servers registered in another user's profile.">i</span></div>
                        <div class="field"><label class="fl">User</label><input id="cfgUser" type="text" placeholder="username" style="flex:1"><input id="cfgPass" type="password" placeholder="password" style="flex:1"><input id="cfgDomain" type="text" placeholder="domain" style="flex:1"></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">Default Update Rate <span class="info" data-tip="Fallback update rate for tags set to 'Source Default' (Tags tab → faceplate → Update Rate). Tags with a specific rate override this.">i</span></div>
                        <div class="field"><label class="fl">Rate</label><select id="cfgUpdateRate"><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000">1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select><button class="btn ghost" id="cfgApplyRate" type="button">Apply</button><span class="msg" id="rateMessage">Applies live</span></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">I/O Mode <span class="info" data-tip="Client-side value-delivery mode for this source, like Matrikon OPC Explorer's per-group I/O selector. AutoDetect I/O: try IOPCDataCallback push, fall back to polling when the server can't. Synchronous I/O: always poll with IOPCSyncIO.Read. Async I/O 2.0: force the push path (even if the global switch is off); falls back to polling with a warning if the server can't provide it. Applied live — no restart.">i</span></div>
                        <div class="field"><label class="fl">Mode</label><select id="cfgIoMode"><option value="AutoDetect">AutoDetect I/O</option><option value="Sync">Synchronous I/O</option><option value="Async20">Async I/O 2.0</option></select><span class="msg" id="ioModeHint" style="font-weight:400;text-transform:none;letter-spacing:0"></span></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">Groups <span class="info" data-tip="Read-only summary of this source's OPC DA groups. Add, edit, rename or delete groups in the DA Groups panel (Manage groups) — changes apply live, no restart.">i</span></div>
                        <div id="cfgGroups" style="display:flex;flex-direction:column;gap:6px"></div>
                        <div class="msg" id="cfgGroupsMsg" style="margin-top:4px"></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">DA Subscriptions <span class="info" data-tip="Global master switch: when OFF, AutoDetect sources never attempt push. When ON, AutoDetect sources use IOPCDataCallback when the server supports it (faster, supports deadband). Sources forced to Async I/O 2.0 always attempt push regardless. Applies on reconnect.">i</span></div>
                        <div class="field"><label class="fl">Global</label><input type="checkbox" id="cfgUseSubscriptions" checked><span class="msg" id="subMessage">Applies on reconnect</span></div>
                    </div>
                    <div class="toolbar" style="margin-top:14px;border-top:1px solid var(--border);padding-top:12px">
                        <button class="btn" id="cfgApply" type="button" style="display:none">Save</button>
                        <button class="btn ghost" id="cfgReset" type="button" style="display:none">Reset</button>
                        <button class="btn ghost" id="cfgNew" type="button">New</button>
                        <button class="btn ghost" id="cfgRemove" type="button">Remove</button>
                    </div>
                </div>
            </div>
        </div>
        <div class="conn-side">
            <div class="box">
                <div class="box-h">Discover Servers</div>
                <div class="box-b">
                    <div class="toolbar">
                        <button class="btn ghost" id="btnReloadServers" type="button">Scan</button>
                        <span class="msg" id="msgServers">Click Use to fill in ProgID + Host.</span>
                    </div>
                    <div class="list" id="listServers" style="max-height:200px"></div>
                </div>
            </div>
            <div class="box">
                <div class="box-h">Saved Connections <span class="msg" id="pSourcesSide" style="margin-left:auto"></span></div>
                <div class="box-b">
                    <div class="list" id="sourcesList" style="max-height:280px"></div>
                </div>
            </div>
            <div class="box">
                <div class="box-h">Backup &amp; Restore</div>
                <div class="box-b">
                    <div class="toolbar">
                        <button class="btn ghost" id="btnExportConfig" type="button">Export Config</button>
                        <button class="btn ghost" id="btnImportConfig" type="button">Import Config</button>
                        <input type="file" id="importConfigFile" accept=".json" style="display:none">
                    </div>
                    <div class="hint" id="configMessage">Export saves all sources, settings, and tag mappings to a JSON file. Passwords are not included — re-enter after import.</div>
                </div>
            </div>
        </div>
    </div>
</div>
<div class="view" id="view-opc-da-groups">
    <div class="box" style="max-width:720px">
        <div class="box-h" style="padding:8px 12px;font-size:13px">OPC DA Groups <span class="msg" id="daGroupsMsg" style="margin-left:12px;font-size:11px"></span><span style="margin-left:auto;display:flex;gap:4px"><button class="btn ghost" type="button" style="height:20px;padding:0 6px;font-size:11px" onclick="expandAllDaGroups()">Expand All</button><button class="btn ghost" type="button" style="height:20px;padding:0 6px;font-size:11px" onclick="collapseAllDaGroups()">Collapse All</button></span></div>
        <div class="box-b" style="padding:10px 12px">
            <div id="daGroupsContainer" style="display:flex;flex-direction:column;gap:8px"></div>
            <div class="hint" style="font-size:11px;margin-top:8px">Each rate is a COM group OpcBridge_&lt;rate&gt; — add/delete per source, set I/O per group. Live apply.</div>
        </div>
    </div>
</div>
<div class="view" id="view-opc-ua">
    <div class="conn-layout">
        <div class="conn-main">
            <div class="box">
                <div class="box-h">OPC UA Configuration <button class="btn" type="button" onclick="openAddSourceWizard()" style="margin-left:auto">+ Add Source</button><span class="msg" id="uaCfgMessage" style="font-weight:400;text-transform:none;letter-spacing:0">Select a saved connection or click New.</span></div>
                <div class="box-b">
                    <div class="field"><label class="fl">Selected</label><select id="uaSelectedSource"></select></div>
                    <div class="conn-section">
                        <div class="conn-section-h">Identity</div>
                        <div class="field"><label class="fl">Source ID <span class="info" data-tip="Unique key with no spaces. Used internally and in UA Node IDs (ns=2;s={sourceId}/...).">i</span></label><input id="uaCfgSourceId" type="text" placeholder="ua-plant-a" style="flex:1"></div>
                        <div class="field"><label class="fl">Name <span class="info" data-tip="Friendly label shown in lists and the Tags tab.">i</span></label><input id="uaCfgDisplayName" type="text" placeholder="Plant UA Server" style="flex:1"></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">Endpoint</div>
                        <div class="field"><label class="fl">Endpoint URL <span class="info" data-tip="opc.tcp URL of the external OPC UA server the bridge connects to as a client (not this bridge's own endpoint).">i</span></label><input id="uaCfgEndpointUrl" type="text" placeholder="opc.tcp://192.168.1.10:4840" style="flex:1"></div>
                        <div class="field"><label class="fl">Security Mode</label><select id="uaCfgSecurityMode"><option value="None">None</option><option value="Sign">Sign</option><option value="SignAndEncrypt">SignAndEncrypt</option></select></div>
                        <div class="field"><label class="fl">Security Policy</label><select id="uaCfgSecurityPolicy"><option value="None">None</option><option value="Basic256Sha256">Basic256Sha256</option></select></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">Credentials <span class="info" data-tip="Optional UserName token. Leave blank for anonymous.">i</span></div>
                        <div class="field"><label class="fl">User</label><input id="uaCfgUser" type="text" placeholder="username" style="flex:1"><input id="uaCfgPass" type="password" placeholder="password" style="flex:1"></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">Update &amp; Scale</div>
                        <div class="field"><label class="fl">Update Rate</label><select id="uaCfgUpdateRate"><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000">1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select></div>
                        <div class="field"><label class="fl">Max Mapped Tags <span class="info" data-tip="Hard cap on mappings for this UA source. Only mapped NodeIds are subscribed.">i</span></label><input id="uaCfgMaxMappedTags" type="number" min="1" value="50000" style="flex:1"></div>
                        <div class="field"><label class="fl">Subscriptions</label><input type="checkbox" id="uaCfgUseSubscriptions" checked><span class="msg" id="uaSubMessage">MonitoredItems for mapped tags</span></div>
                        <div class="field"><label class="fl">Read Mode <span class="info" data-tip="How values are delivered right now: async (subscription) = MonitoredItems push; sync (polling) = polling. Follows the Subscriptions checkbox on reconnect.">i</span></label><span class="msg" id="uaCfgReadMode" style="font-weight:400;text-transform:none;letter-spacing:0">—</span></div>
                        <div class="field"><label class="fl">Write Mode <span class="info" data-tip="How writes are sent: always a synchronous request/response via the UA Write service.">i</span></label><span class="msg" id="uaCfgWriteMode" style="font-weight:400;text-transform:none;letter-spacing:0">—</span></div>
                    </div>
                    <div class="toolbar" style="margin-top:14px;border-top:1px solid var(--border);padding-top:12px">
                        <button class="btn" id="btnUaTestConnection" type="button">Test Connection</button>
                        <button class="btn" id="uaCfgApply" type="button" style="display:none">Save</button>
                        <button class="btn ghost" id="uaCfgReset" type="button" style="display:none">Reset</button>
                        <button class="btn ghost" id="uaCfgNew" type="button">New</button>
                        <button class="btn ghost" id="uaCfgRemove" type="button">Remove</button>
                    </div>
                </div>
            </div>
        </div>
        <div class="conn-side">
            <div class="box">
                <div class="box-h">Discover UA Servers</div>
                <div class="box-b">
                    <div class="field"><label class="fl">Discovery URL <span class="info" data-tip="opc.tcp URL of a Local Discovery Server (LDS) or any known UA server to probe. Leave blank to use the Endpoint URL field, or opc.tcp://localhost:4840.">i</span></label><input id="uaDiscoverUrl" type="text" placeholder="opc.tcp://localhost:4840" style="flex:1"></div>
                    <div class="toolbar">
                        <button class="btn ghost" id="btnUaDiscover" type="button">Scan</button>
                        <span class="msg" id="msgUaDiscover">Click Scan to find servers. Use fills Endpoint URL.</span>
                    </div>
                    <div class="list" id="listUaDiscover" style="max-height:200px"></div>
                </div>
            </div>
            <div class="box">
                <div class="box-h">Saved UA Connections <span class="msg" id="pUaSourcesSide" style="margin-left:auto"></span></div>
                <div class="box-b">
                    <div class="list" id="uaSourcesList" style="max-height:280px"></div>
                </div>
            </div>
            <div class="box">
                <div class="box-h">Notes</div>
                <div class="box-b">
                    <div class="hint">OPC UA <b>source</b> = bridge connects outbound to an external UA server. The Monitor page OPC UA endpoint is the bridge's own server for clients.</div>
                </div>
            </div>
        </div>
    </div>
</div>
<div class="view" id="view-ua-subs">
    <div class="box" style="max-width:720px">
        <div class="box-h" style="padding:8px 12px;font-size:13px">UA Subscriptions <span class="msg" id="subsMsg" style="margin-left:12px;font-size:11px"></span><span style="margin-left:auto;display:flex;gap:4px"><button class="btn ghost" type="button" style="height:20px;padding:0 6px;font-size:11px" onclick="expandAllUaSubs()">Expand All</button><button class="btn ghost" type="button" style="height:20px;padding:0 6px;font-size:11px" onclick="collapseAllUaSubs()">Collapse All</button></span></div>
        <div class="box-b" style="padding:10px 12px">
            <div id="uaSubsContainer" style="display:flex;flex-direction:column;gap:8px"></div>
            <div class="hint" style="font-size:11px;margin-top:8px">Tags assigned to a named subscription publish at that rate; unassigned tags ride the read-only Default tile (source Update Rate). Removing a subscription moves its tags back to default.</div>
        </div>
    </div>
</div>
<div class="modal-overlay" id="dagModal" onclick="if(event.target===this)closeDagModal()">
    <div class="modal" style="width:min(420px,94vw)">
        <div class="modal-h"><div class="n" id="dagModalTitle">Add Group</div><button class="modal-close" type="button" onclick="closeDagModal()">×</button></div>
        <div class="modal-b">
            <div class="field"><label class="fl">Source</label><span class="msg" id="dagModalSource" style="font-family:'Consolas',monospace"></span></div>
            <div class="field"><label class="fl">Name</label><input id="dagModalName" type="text" placeholder="OpcBridge_1000" style="flex:1"></div>
            <div class="field"><label class="fl">Rate</label><select id="dagModalRate" style="flex:1"><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000">1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select></div>
            <div class="field"><label class="fl">I/O Mode</label><select id="dagModalIo" style="flex:1"><option value="AutoDetect">AutoDetect</option><option value="Sync">Sync</option><option value="Async20">Async20</option></select></div>
            <div class="msg" id="dagModalMsg"></div>
        </div>
        <div class="modal-f"><button class="btn ghost" type="button" onclick="closeDagModal()">Cancel</button><button class="btn" type="button" id="dagModalSaveBtn" onclick="dagModalSave()">Save</button></div>
    </div>
</div>
<div class="modal-overlay" id="uaSubModal" onclick="if(event.target===this)closeUaSubModal()">
    <div class="modal" style="width:min(420px,94vw)">
        <div class="modal-h"><div class="n" id="uaSubModalTitle">Add Subscription</div><button class="modal-close" type="button" onclick="closeUaSubModal()">×</button></div>
        <div class="modal-b">
            <div class="field"><label class="fl">Source</label><span class="msg" id="uaSubModalSource" style="font-family:'Consolas',monospace"></span></div>
            <div class="field"><label class="fl">Name</label><input id="uaSubModalName" type="text" placeholder="fast" style="flex:1"></div>
            <div class="field"><label class="fl">Rate</label><select id="uaSubModalRate" style="flex:1"><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000">1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select></div>
            <div class="msg" id="uaSubModalMsg"></div>
        </div>
        <div class="modal-f"><button class="btn ghost" type="button" onclick="closeUaSubModal()">Cancel</button><button class="btn" type="button" id="uaSubModalSaveBtn" onclick="uaSubModalSave()">Save</button></div>
    </div>
</div>
<div class="modal-overlay" id="addSourceWizard" onclick="if(event.target===this)closeAddSourceWizard()">
    <div class="modal wizard" role="dialog" aria-modal="true" aria-labelledby="addSourceWizardTitle">
        <div class="modal-head">
            <div class="modal-title" id="addSourceWizardTitle">Add Source</div>
            <button class="modal-close" type="button" onclick="closeAddSourceWizard()">&times;</button>
        </div>
        <div class="wizard-steps">
            <span class="wizard-step" data-step="1">1. Type</span>
            <span class="wizard-step" data-step="2">2. Identity</span>
            <span class="wizard-step" data-step="3">3. Server</span>
            <span class="wizard-step" data-step="4">4. Auth</span>
            <span class="wizard-step" data-step="5">5. Defaults</span>
            <span class="wizard-step" data-step="6">6. Review</span>
        </div>
        <div class="wizard-body">
            <div class="wizard-pane active" data-pane="1">
                <div class="field"><label class="fl">Source Type</label>
                    <select id="wzSourceType" onchange="wzOnTypeChange()">
                        <option value="OpcDa">OPC DA</option>
                        <option value="OpcUa">OPC UA</option>
                    </select>
                </div>
                <div class="hint">OPC DA uses ProgID/Host (Windows COM). OPC UA uses an opc.tcp endpoint (cross-platform client).</div>
            </div>
            <div class="wizard-pane" data-pane="2">
                <div class="field"><label class="fl">Source ID</label><input type="text" id="wzSourceId" placeholder="server-a"></div>
                <div class="field"><label class="fl">Display Name</label><input type="text" id="wzDisplayName" placeholder="(optional)"></div>
                <div class="hint">Unique key with no spaces. Used in UA Node IDs (ns=2;s={sourceId}/...).</div>
            </div>
            <div class="wizard-pane" data-pane="3">
                <div id="wzDaServerFields">
                    <div class="field"><label class="fl">Host</label><input type="text" id="wzHost" placeholder="localhost"></div>
                    <div class="field"><label class="fl">ProgID / CLSID</label><input type="text" id="wzProgId" placeholder="Kepware.KEPServerEX.V6"></div>
                    <button class="btn ghost" type="button" onclick="wzBrowseServers()">Browse Servers</button>
                    <span class="msg" id="wzMsgServers"></span>
                    <div class="list" id="wzListServers" style="max-height:180px"></div>
                </div>
                <div id="wzUaServerFields" style="display:none">
                    <div class="field"><label class="fl">Endpoint URL</label><input type="text" id="wzEndpointUrl" placeholder="opc.tcp://host:4840"></div>
                    <div class="field"><label class="fl">Security Mode</label><select id="wzSecurityMode"><option value="None">None</option><option value="Sign">Sign</option><option value="SignAndEncrypt">SignAndEncrypt</option></select></div>
                    <div class="field"><label class="fl">Security Policy</label><select id="wzSecurityPolicy"><option value="None">None</option><option value="Basic256Sha256">Basic256Sha256</option></select></div>
                </div>
            </div>
            <div class="wizard-pane" data-pane="4">
                <div id="wzDaAuthFields">
                    <div class="field"><label class="fl">Domain</label><input type="text" id="wzDomain" placeholder="(optional)"></div>
                    <div class="field"><label class="fl">Username</label><input type="text" id="wzUser" placeholder="(optional)"></div>
                    <div class="field"><label class="fl">Password</label><input type="password" id="wzPass"></div>
                    <div class="hint">Only required for remote DCOM or servers in another user's profile.</div>
                </div>
                <div id="wzUaAuthFields" style="display:none">
                    <div class="field"><label class="fl">UA Username</label><input type="text" id="wzUaUser" placeholder="(optional, anonymous if empty)"></div>
                    <div class="field"><label class="fl">UA Password</label><input type="password" id="wzUaPass"></div>
                    <div class="hint">UserName token credentials for the external OPC UA server.</div>
                </div>
            </div>
            <div class="wizard-pane" data-pane="5">
                <div class="field"><label class="fl">Update Rate</label><select id="wzUpdateRate"><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000" selected>1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select></div>
                <div class="field"><label class="fl">Subscriptions</label><input type="checkbox" id="wzSubs" checked> <span class="msg" id="wzSubsHint">Use IOPCDataCallback (recommended)</span></div>
                <div class="field" id="wzMaxTagsField" style="display:none"><label class="fl">Max Mapped Tags</label><input type="number" id="wzMaxMappedTags" min="1" value="50000"></div>
            </div>
            <div class="wizard-pane" data-pane="6">
                <div class="wizard-summary" id="wzSummary"></div>
                <div class="hint">Click Finish to save. You can map tags next.</div>
            </div>
        </div>
        <div class="wizard-foot">
            <button class="btn ghost" type="button" onclick="closeAddSourceWizard()">Cancel</button>
            <button class="btn ghost" type="button" id="wzBack" onclick="wzStep(-1)">Back</button>
            <button class="btn" type="button" id="wzNext" onclick="wzStep(1)">Next</button>
            <button class="btn" type="button" id="wzFinish" style="display:none" onclick="wzFinish()">Finish &amp; Save</button>
        </div>
    </div>
</div>
<div class="view" id="view-mx-component">
    <div class="conn-layout">
        <div class="conn-main">
            <div class="box">
                <div class="box-h">MELSOFT MX Component 4 <button class="btn" type="button" onclick="newMxSource()" style="margin-left:auto">+ Add Connection</button><span class="msg" id="mxMessage" style="font-weight:400;text-transform:none;letter-spacing:0">Select an MX Component connection or click New.</span></div>
                <div class="box-b">
                    <div class="conn-section">
                        <div class="conn-section-h">Connection <span class="info" data-tip="MX Component is a local COM driver that owns the physical link to the PLC. Configure that link (serial, Ethernet, or GX Simulator) once in MX Component's own Communication Settings Utility — it assigns a logical station number that this app references. GX Simulator note: it uses session-bound shared memory, so the bridge must run in the same logged-in interactive Windows session (Interactive task logon — S4U/service mode cannot reach it).">i</span></div>
                        <div class="field" style="display:block;background:var(--bg);border:1px solid var(--border2);border-radius:5px;padding:9px 11px;color:var(--muted)">The physical link (serial RS-422/RS-232C, Ethernet, or <b>GX Simulator</b>) is configured <b>once in MX Component's own Communication Settings Utility</b> — this app only needs the <b>logical station number</b> (0–1023) that the utility assigned. For an <b>A3NCPU</b>: pick <b>A series → A3N</b> in the utility's wizard (1C frame); if "A series" is missing, an <b>FX-series CPU type</b> usually still talks to it. <b>GX Simulator</b> is session-bound — the bridge must run in the same logged-in desktop session, so the Windows scheduled task needs <b>Interactive</b> logon (register with <span class="mono">-LogonType Interactive</span>).</div>
                        <div class="field"><label class="fl">Logical Station</label><input id="mxStation" type="number" min="0" max="1023" value="0" style="width:90px"><span class="msg">Assigned in the MX Component Communication Settings Utility.</span></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">Identity</div>
                        <div class="field"><label class="fl">Source ID <span class="info" data-tip="Unique key with no spaces. Used internally and in UA Node IDs (ns=2;s={sourceId}/...).">i</span></label><input id="mxSourceId" type="text" placeholder="plc-mx-1" style="flex:1"></div>
                        <div class="field"><label class="fl">Name <span class="info" data-tip="Friendly label shown in lists and the Tags tab.">i</span></label><input id="mxName" type="text" placeholder="Line 1 PLC" style="flex:1"></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">Defaults</div>
                        <div class="field"><label class="fl">Timeout ms</label><input id="mxTimeout" type="number" min="100" step="100" value="3000" style="width:100px">
                        <label class="fl" style="width:auto">Retries</label><input id="mxRetry" type="number" min="0" max="10" value="2" style="width:70px"></div>
                        <div class="field"><label class="fl">Update Rate</label><select id="mxRate"><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000" selected>1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select>
                        <label class="fl" style="width:auto">Max tags <span class="info" data-tip="Safety limit on mapped tags for this source; adding mappings beyond it is rejected.">i</span></label><input id="mxMaxTags" type="number" min="1" step="1" value="2000" style="width:90px"></div>
                    </div>
                    <div class="toolbar" style="margin-top:14px;border-top:1px solid var(--border);padding-top:12px">
                        <button class="btn" id="mxSave" type="button">Save</button>
                        <button class="btn ghost" id="mxReset" type="button">Reset</button>
                        <button class="btn ghost" id="mxNew" type="button">New</button>
                        <button class="btn ghost" id="mxRemove" type="button">Remove</button>
                        <button class="btn ghost" id="mxTest" type="button">Test connection</button>
                    </div>
                </div>
            </div>
        </div>
        <div class="conn-side">
            <div class="box">
                <div class="box-h">MX Component Connections <span class="msg" id="mxCount" style="margin-left:auto"></span></div>
                <div class="box-b">
                    <div class="list" id="mxList" style="max-height:280px"></div>
                </div>
            </div>
            <div class="box">
                <div class="box-h">Addressing</div>
                <div class="box-b">
                    <div class="hint">Map tags on the Tags page with device addresses, e.g. <span class="mono">D100</span>, <span class="mono">M10</span>, <span class="mono">X20</span>, <span class="mono">D100:8</span>.</div>
                    <button class="btn ghost" type="button" id="mxRangesToggle" style="margin-top:8px" onclick="toggleAddressRanges('mxAddressRanges', this)">Show accepted addresses ▾</button>
                    <div id="mxAddressRanges" style="display:none"></div>
                </div>
            </div>
        </div>
    </div>
</div>
<div class="view" id="view-drivers">
    <div class="conn-layout">
        <div class="conn-main">
            <div class="box">
                <div class="box-h">PLC Driver <button class="btn" type="button" onclick="openDriverWizard()" style="margin-left:auto">+ Add Driver</button><span class="msg" id="drvA3nMessage" style="font-weight:400;text-transform:none;letter-spacing:0">Select a driver source or click New.</span></div>
                <div class="box-b">
                    <div class="conn-section">
                        <div class="conn-section-h">Identity</div>
                        <div class="field"><label class="fl">Source ID <span class="info" data-tip="Unique key with no spaces. Used internally and in UA Node IDs (ns=2;s={sourceId}/...).">i</span></label><input id="drvA3nSourceId" type="text" placeholder="plc-a3n-1" style="flex:1"></div>
                        <div class="field"><label class="fl">Name <span class="info" data-tip="Friendly label shown in lists and the Tags tab.">i</span></label><input id="drvA3nName" type="text" placeholder="Line 1 PLC" style="flex:1"></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">Serial Port <span class="info" data-tip="RS-422/RS-232C link to the A3N CPU (1C protocol, Format 1). Defaults: 9600 baud, 8 data bits, odd parity, 1 stop bit.">i</span></div>
                        <div class="field"><label class="fl">Port</label><input id="drvA3nPort" type="text" placeholder="COM3 or /dev/ttyUSB0" style="flex:1"><button class="btn ghost" id="btnDrvScanPorts" type="button">Scan</button></div>
                        <div class="list" id="listDrvPorts" style="max-height:120px;margin:0 0 8px 0"></div>
                        <span class="msg" id="msgDrvPorts">Click Scan to list host serial ports.</span>
                        <div class="field"><label class="fl">Baud</label><select id="drvA3nBaud"><option value="1200">1200</option><option value="2400">2400</option><option value="4800">4800</option><option value="9600" selected>9600</option><option value="19200">19200</option></select>
                        <label class="fl" style="width:auto">Data bits</label><select id="drvA3nDataBits"><option value="7">7</option><option value="8" selected>8</option></select></div>
                        <div class="field"><label class="fl">Parity</label><select id="drvA3nParity"><option value="None">None</option><option value="Odd" selected>Odd</option><option value="Even">Even</option></select>
                        <label class="fl" style="width:auto">Stop bits</label><select id="drvA3nStopBits"><option value="One" selected>1</option><option value="Two">2</option></select></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">PLC Addressing <span class="info" data-tip="Station 00 = directly attached CPU. PC number FF = own station (1C protocol).">i</span></div>
                        <div class="field" id="drvA3nStationRow"><label class="fl">Station</label><input id="drvA3nStation" type="text" placeholder="00" maxlength="2" style="width:70px">
                        <label class="fl" style="width:auto">PC No</label><input id="drvA3nPc" type="text" placeholder="FF" maxlength="2" style="width:70px"></div>
                        <div class="field" id="drvS7PpiRow" style="display:none"><label class="fl">Local PPI</label><input id="drvS7LocalPpi" type="number" min="0" max="126" value="0" style="width:70px">
                        <label class="fl" style="width:auto">Remote PPI</label><input id="drvS7RemotePpi" type="number" min="0" max="126" value="2" style="width:70px"></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">Defaults</div>
                        <div class="field"><label class="fl">Timeout ms</label><input id="drvA3nTimeout" type="number" min="100" step="100" value="3000" style="width:100px">
                        <label class="fl" style="width:auto">Retries</label><input id="drvA3nRetry" type="number" min="0" max="10" value="2" style="width:70px"></div>
                        <div class="field"><label class="fl">Update Rate</label><select id="drvA3nRate"><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000" selected>1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select>
                        <label class="fl" style="width:auto">Max tags <span class="info" data-tip="Safety limit on mapped tags for this serial link; adding mappings beyond it is rejected.">i</span></label><input id="drvA3nMaxTags" type="number" min="1" step="1" value="2000" style="width:90px"></div>
                    </div>
                    <div class="toolbar" style="margin-top:14px;border-top:1px solid var(--border);padding-top:12px">
                        <button class="btn" id="drvA3nSave" type="button">Save</button>
                        <button class="btn ghost" id="drvA3nReset" type="button">Reset</button>
                        <button class="btn ghost" id="drvA3nNew" type="button">New</button>
                        <button class="btn ghost" id="drvA3nRemove" type="button">Remove</button>
                        <button class="btn ghost" id="drvA3nTest" type="button">Test connection</button>
                    </div>
                </div>
            </div>
        </div>
        <div class="conn-side">
            <div class="box">
                <div class="box-h">Driver Sources <span class="msg" id="drvA3nCount" style="margin-left:auto"></span></div>
                <div class="box-b">
                    <div class="list" id="drvA3nList" style="max-height:280px"></div>
                </div>
            </div>
            <div class="box">
                <div class="box-h">Addressing</div>
                <div class="box-b">
                    <div class="hint">Map tags on the Tags page with device addresses, e.g. <span class="mono">D100</span>, <span class="mono">M10</span>, <span class="mono">X20</span>, <span class="mono">D100:8</span>.</div>
                </div>
            </div>
        </div>
    </div>
</div>
<div class="modal-overlay" id="wzDrv" onclick="if(event.target===this)closeDriverWizard()">
    <div class="modal wizard" role="dialog" aria-modal="true" aria-labelledby="wzDrvTitle">
        <div class="modal-head">
            <div class="modal-title" id="wzDrvTitle">Add PLC Driver Source</div>
            <button class="modal-close" type="button" onclick="closeDriverWizard()">&times;</button>
        </div>
        <div class="wizard-steps">
            <span class="wzdrv-step" data-step="1">1. Type</span>
            <span class="wzdrv-step" data-step="2">2. Identity</span>
            <span class="wzdrv-step" data-step="3">3. Serial</span>
            <span class="wzdrv-step" data-step="4">4. Defaults</span>
            <span class="wzdrv-step" data-step="5">5. Review</span>
        </div>
        <div class="wizard-body">
            <div class="wzdrv-pane active" data-pane="1">
                <div class="field"><label class="fl">Driver Type</label><select id="wzDrvType" onchange="wzDrvOnTypeChange()"><option value="MelsecA3n">Mitsubishi Melsec A3N (serial 1C)</option><option value="S7200Ppi">Siemens S7-200 (PPI serial)</option></select></div>
                <div class="hint">Serial link to the PLC CPU (RS-422/RS-232C, 1C protocol). For MELSOFT MX Component 4, use the MX Component tab — its link is configured in MX Component's own Communication Settings Utility.</div>
            </div>
            <div class="wzdrv-pane" data-pane="2">
                <div class="field"><label class="fl">Source ID</label><input type="text" id="wzDrvSourceId" placeholder="plc-a3n-1"></div>
                <div class="field"><label class="fl">Display Name</label><input type="text" id="wzDrvName" placeholder="(optional)"></div>
                <div class="hint">Unique key with no spaces. Used in UA Node IDs (ns=2;s={sourceId}/...).</div>
            </div>
            <div class="wzdrv-pane" data-pane="3">
                <div class="field"><label class="fl">Serial Port</label><input type="text" id="wzDrvPort" placeholder="COM3 or /dev/ttyUSB0" style="flex:1"><button class="btn ghost" id="btnWzDrvScanPorts" type="button">Scan</button></div>
                <div class="list" id="listWzDrvPorts" style="max-height:120px;margin:0 0 8px 0"></div>
                <span class="msg" id="msgWzDrvPorts">Click Scan to list host serial ports.</span>
                <div class="field"><label class="fl">Baud</label><select id="wzDrvBaud"><option value="1200">1200</option><option value="2400">2400</option><option value="4800">4800</option><option value="9600" selected>9600</option><option value="19200">19200</option></select>
                <label class="fl" style="width:auto">Data bits</label><select id="wzDrvDataBits"><option value="7">7</option><option value="8" selected>8</option></select></div>
                <div class="field"><label class="fl">Parity</label><select id="wzDrvParity"><option value="None">None</option><option value="Odd" selected>Odd</option><option value="Even">Even</option></select>
                <label class="fl" style="width:auto">Stop bits</label><select id="wzDrvStopBits"><option value="One" selected>1</option><option value="Two">2</option></select></div>
                <div class="field" id="wzDrvStationRow"><label class="fl">Station</label><input type="text" id="wzDrvStation" placeholder="00" maxlength="2" style="width:70px">
                <label class="fl" style="width:auto">PC No</label><input type="text" id="wzDrvPc" placeholder="FF" maxlength="2" style="width:70px"></div>
                <div class="field" id="wzDrvS7PpiRow" style="display:none"><label class="fl">Local PPI</label><input type="number" id="wzDrvLocalPpi" min="0" max="126" value="0" style="width:70px">
                <label class="fl" style="width:auto">Remote PPI</label><input type="number" id="wzDrvRemotePpi" min="0" max="126" value="2" style="width:70px"></div>
            </div>
            <div class="wzdrv-pane" data-pane="4">
                <div class="field"><label class="fl">Timeout ms</label><input type="number" id="wzDrvTimeout" min="100" step="100" value="3000" style="width:100px">
                <label class="fl" style="width:auto">Retries</label><input type="number" id="wzDrvRetry" min="0" max="10" value="2" style="width:70px"></div>
                <div class="field"><label class="fl">Update Rate</label><select id="wzDrvRate"><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000" selected>1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select>
                <label class="fl" style="width:auto">Max tags</label><input type="number" id="wzDrvMaxTags" min="1" step="1" value="2000" style="width:90px"></div>
            </div>
            <div class="wzdrv-pane" data-pane="5">
                <div class="wizard-summary" id="wzDrvSummary"></div>
                <div class="hint">Click Finish to save. You can map tags next.</div>
            </div>
        </div>
        <div class="wizard-foot">
            <button class="btn ghost" type="button" onclick="closeDriverWizard()">Cancel</button>
            <button class="btn ghost" type="button" id="wzDrvBack" onclick="wzDrvStep(-1)">Back</button>
            <button class="btn" type="button" id="wzDrvNext" onclick="wzDrvStep(1)">Next</button>
            <button class="btn" type="button" id="wzDrvFinish" style="display:none" onclick="wzDrvFinish()">Finish &amp; Save</button>
        </div>
    </div>
</div>
<div class="view" id="view-tags">
    <div class="first-run-banner" id="bannerTagsNoSources" style="display:none"></div>
    <div class="map-type-tabs" id="mapTypeTabs">
        <button class="map-type-tab active" type="button" data-map-type="opc-da" onclick="setMapType('opc-da')">OPC DA</button>
        <button class="map-type-tab" type="button" data-map-type="opc-ua" onclick="setMapType('opc-ua')">OPC UA</button>
        <button class="map-type-tab" type="button" data-map-type="drivers" onclick="setMapType('drivers')">Drivers</button>
        <button class="map-type-tab" type="button" data-map-type="mx" onclick="setMapType('mx')">MX</button>
    </div>
    <div class="box" style="margin-bottom:14px">
        <div class="box-h">Tag Browser</div>
        <div class="box-b">
            <div class="field" style="margin-bottom:10px">
                <label class="fl">Source</label>
                <select id="mapSourceSelect"></select>
                <span class="msg" id="tagSourceStatus"></span>
                <span class="msg" id="mapSourceHint"></span>
            </div>
            <div style="margin:-6px 0 8px 0">
                <button class="btn ghost" type="button" id="mapAddressRangesToggle" style="display:none;padding:3px 9px;font-size:11px" onclick="toggleAddressRanges('mapAddressRanges', this)">Show accepted addresses ▾</button>
                <div id="mapAddressRanges" style="display:none"></div>
            </div>
            <div class="tag-browser-toolbar" id="mapBrowseToolbar">
                <button class="btn" id="btnBrowseAllTags" type="button">Browse All Tags</button>
                <button class="btn ghost" id="btnBrowseTags" type="button">Browse Folders</button>
                <span class="msg" id="tagStatus">Browse all tags, or open folders one level at a time.</span>
            </div>
            <div class="breadcrumb" id="tagBreadcrumb"></div>
            <div class="list" id="tagTree"></div>
        </div>
    </div>
    <div class="box">
        <div class="box-h">Source → OPC UA Mappings <span class="msg" id="mapCount" style="margin-left:auto"></span></div>
        <div class="box-b">
            <div class="add-mapping-box">
                <div class="field">
                    <input id="manualItem" type="text" placeholder="Item ID (e.g. Random.Real8, ns=2;s=Tag, D100)" style="flex:1">
                    <input id="manualUaNodeId" type="text" placeholder="UA NodeId (optional)" style="flex:1">
                </div>
                <div class="field" style="margin-bottom:0">
                    <button class="btn" type="button" id="manualAdd">Add Mapping</button>
                    <span class="msg">Or browse tags above and click Add.</span>
                </div>
            </div>
            <div class="hint" id="mappingMessage" style="margin-bottom:10px">Click a tag to open its faceplate. Disable a tag to stop publishing it, or set a manual value to override the source.</div>
            <div class="mapping-toolbar">
                <input id="mappingFilter" type="text" placeholder="Filter by name, item ID, UA node, source…" style="flex:1;min-width:120px">
                <label class="fl" style="width:auto">Sort</label>
                <select id="mappingSort">
                    <option value="name">Name</option>
                    <option value="source">Server (Source)</option>
                    <option value="item">Item ID</option>
                    <option value="node">UA Node</option>
                    <option value="description">Description</option>
                    <option value="access">Access Mode</option>
                    <option value="rate">Poll Rate</option>
                    <option value="deadband">Deadband</option>
                    <option value="status">Status (Enabled first)</option>
                </select>
                <button class="btn ghost" type="button" id="mappingSortDir" title="Toggle sort direction">↑</button>
            </div>
            <div class="list" id="mappedList"></div>
        </div>
    </div>
</div>
<div class="view" id="view-interlinks">
    <div class="box">
        <div class="box-h">Interlinks <span class="msg" id="linksCount" style="margin-left:auto"></span></div>
        <div class="box-b">
            <div class="hint" id="linksMessage" style="margin-bottom:10px">Create tag-to-tag rules between sources here. Interlinks are a separate subsystem from OPC UA tag mappings.</div>
            <div class="fp-body" style="margin-bottom:10px">
                <div class="fp-panel">
                    <div class="fp-k">Consumer</div>
                    <select id="interlinkConsumerSource" style="width:100%;margin-bottom:8px" onchange="onInterlinkSourceChange('consumer')"></select>
                    <div class="list" id="interlinkConsumerList" style="max-height:220px"><span class="msg">Select a source to list its Maps tags.</span></div>
                </div>
                <div class="fp-panel">
                    <div class="fp-k">Provider</div>
                    <select id="interlinkProviderSource" style="width:100%;margin-bottom:8px" onchange="onInterlinkSourceChange('provider')"></select>
                    <div class="list" id="interlinkProviderList" style="max-height:220px"><span class="msg">Select a source to list its Maps tags.</span></div>
                </div>
            </div>
            <div class="tag-browser-toolbar">
                <button class="btn" type="button" id="btnSetLink">Save Link</button>
                <button class="btn ghost" type="button" id="btnClearLink">Delete Saved Link</button>
                <button class="btn ghost" type="button" id="btnClearLinkSelection">Clear Selection</button>
                <span class="msg" id="interlinkStatus">Pick both endpoints from Maps tags — OPC DA, OPC UA or MX Component — so every saved interlink can carry values.</span>
            </div>
            <div class="list" id="linksList" style="margin-top:10px"></div>
        </div>
    </div>
</div>
<div class="modal-overlay" id="faceplateOverlay" onclick="if(event.target===this)closeFaceplate()">
    <div class="modal">
        <div class="modal-h">
            <div><div class="n" id="fpName"></div><div class="p" id="fpSub"></div></div>
            <button class="modal-close" type="button" onclick="closeFaceplate()">&times;</button>
        </div>
        <div class="modal-b">
            <div class="fp-panel" id="fpLivePanel" style="margin-bottom:12px"></div>
            <div class="fp-subtabs">
                <button class="fp-subtab active" type="button" data-fptab="basic" onclick="showFpTab('basic')">Basic</button>
                <button class="fp-subtab" type="button" data-fptab="setup" onclick="showFpTab('setup')">Setup</button>
                <button class="fp-subtab" type="button" data-fptab="sim" onclick="showFpTab('sim')">Simulation</button>
                <button class="fp-subtab" type="button" data-fptab="mqtt" onclick="showFpTab('mqtt')">MQTT</button>
                <button class="fp-subtab" type="button" data-fptab="influx" onclick="showFpTab('influx')">Influx</button>
            </div>
            <div class="fp-tabpane" id="fp-pane-basic">
                <div class="field"><label class="fl">Tag Name</label><input type="text" id="fpDisplayName" style="flex:1"></div>
                <div class="field"><label class="fl">Item ID</label><input type="text" id="fpDaItemId" readonly style="flex:1;opacity:.72"></div>
                <div class="field"><label class="fl">UA Node</label><input type="text" id="fpUaNodeId" readonly style="flex:1;opacity:.72"></div>
                <div class="field"><label class="fl">Description</label><input type="text" id="fpDescription" placeholder="Operator notes / tag description (optional)" style="flex:1"></div>
            </div>
            <div class="fp-tabpane" id="fp-pane-setup" style="display:none">
                <div class="field"><label class="fl">Access Rights</label><select id="fpAccess" data-action="tag-access"><option value="Read">Read (Source → UA)</option><option value="Read-Write">Read-Write (Source ↔ UA)</option><option value="Write">Write (UA → Source)</option></select></div>
                <div class="field"><label class="fl">Enabled</label><input type="checkbox" id="fpEnabled" data-action="toggle-tag-enabled"></div>
                <div class="field" id="fpSubscriptionField" style="display:none"><label class="fl">Subscription</label><select id="fpSubscription"></select><span class="msg" id="fpSubscriptionHint"></span></div>
                <div class="field"><label class="fl">Update Rate</label><select id="fpPollRate" data-action="tag-poll-rate"><option value="0">Source Default</option><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000">1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select></div>
                <div class="field"><label class="fl">Deadband %</label><input type="number" id="fpDeadband" min="0" max="100" step="0.1" value="0" style="width:80px"></div>
                <div class="hint" style="margin-top:4px">Update Rate = source poll/publish interval. With subscriptions on, the source pushes changes at this rate when supported. With subscriptions off, the bridge polls at this rate.</div>
            </div>
            <div class="fp-tabpane" id="fp-pane-sim" style="display:none">
                <div class="field"><label class="fl">Simulated</label><input type="checkbox" id="fpSimulated" data-action="tag-simulated"></div>
                <div class="field"><label class="fl">Manual Value</label><input type="text" id="fpManualInput" data-action="tag-manual-value" disabled style="flex:1"></div>
                <div class="hint" id="fpModeHint" style="margin-top:4px"></div>
            </div>
            <div class="fp-tabpane" id="fp-pane-mqtt" style="display:none">
                <div class="field"><label class="fl">MQTT</label><input type="checkbox" id="fpMqttEnabled"> <span class="msg">publish/subscribe this tag</span></div>
                <div class="field"><label class="fl">MQTT Topic</label><input type="text" id="fpMqttTopic" placeholder="override topic (optional)"></div>
                <div class="hint" style="margin-top:4px">When enabled, the tag's value is published to the broker and inbound broker writes are applied to it. Leave the topic blank to use the default <span class="mono">{TopicPrefix}/{SourceId}/{ItemId}</span> scheme.</div>
            </div>
            <div class="fp-tabpane" id="fp-pane-influx" style="display:none">
                <div class="field"><label class="fl">Influx log</label><input type="checkbox" id="fpInfluxEnabled"> <span class="msg">write this tag to InfluxDB</span></div>
                <div class="hint" style="margin-top:4px">When enabled, each value change of this tag is written to the configured InfluxDB bucket.</div>
            </div>
        </div>
        <div class="modal-f">
            <button class="btn ghost" type="button" id="fpRemove" data-action="remove-mapping">Remove</button>
            <button class="btn" type="button" id="fpApply" data-action="save-tag">Apply</button>
        </div>
    </div>
</div>
<div class="view" id="view-logs">
    <div class="box">
        <div class="box-h">Recent Logs <span class="msg" id="logMessage" style="margin-left:auto;font-weight:400;text-transform:none;letter-spacing:0">Showing recent in-app logs.</span></div>
        <div class="box-b log-panel">
            <div class="toolbar">
                <button class="btn ghost" id="btnRefreshLogs" type="button">Refresh</button>
                <label class="fl" for="logLevel" style="width:auto">Minimum Level</label>
                <select id="logLevel">
                    <option value="Trace">Trace</option>
                    <option value="Debug">Debug</option>
                    <option value="Information" selected>Information</option>
                    <option value="Warning">Warning</option>
                    <option value="Error">Error</option>
                    <option value="Critical">Critical</option>
                </select>
                <label class="fl" for="logAutoRefresh" style="width:auto">Auto-refresh</label>
                <input type="checkbox" id="logAutoRefresh" checked>
                <label class="fl" for="logLimit" style="width:auto">Limit</label>
                <select id="logLimit">
                    <option value="50">50</option>
                    <option value="200" selected>200</option>
                    <option value="500">500 (max)</option>
                </select>
            </div>
            <div class="log-view" id="logEntries"><span class="msg">Loading logs…</span></div>
        </div>
    </div>
</div>
<div class="view" id="view-help">
    <div class="help-subtabs">
        <button class="help-subtab active" onclick="switchHelpSubTab('getting-started')">Getting Started</button>
        <button class="help-subtab" onclick="switchHelpSubTab('features')">Features</button>
        <button class="help-subtab" onclick="switchHelpSubTab('reference')">Reference</button>
    </div>
    <div class="help-subtab-content active" id="help-getting-started">
        <div class="help-accordion" id="helpContent1"><span class="msg">Loading help…</span></div>
    </div>
    <div class="help-subtab-content" id="help-features">
        <div class="help-accordion" id="helpContent2"></div>
    </div>
    <div class="help-subtab-content" id="help-reference">
        <div class="help-accordion" id="helpContent3"></div>
    </div>
</div>
<div class="view" id="view-about">
    <div class="box">
        <div class="box-h">About This App</div>
        <div class="box-b">
            <div class="kv">
                <div class="k">Application</div><div class="v" id="aboutName">—</div>
                <div class="k">Version</div><div class="v" id="aboutVersion">—</div>
                <div class="k">Informational Build</div><div class="v" id="aboutInfoVersion">—</div>
                <div class="k">Framework</div><div class="v" id="aboutFramework">—</div>
                <div class="k">Architecture</div><div class="v" id="aboutArchitecture">—</div>
                <div class="k">Operating System</div><div class="v" id="aboutOs">—</div>
                <div class="k">Machine</div><div class="v" id="aboutMachine">—</div>
                <div class="k">Creator</div><div class="v" id="aboutCreator">—</div>
                <div class="k">Section</div><div class="v" id="aboutSection">—</div>
            </div>
        </div>
    </div>
</div>
<div class="view" id="view-mqtt">
    <div class="first-run-banner" id="hintMqtt" style="display:none"></div>
    <div class="grid2">
        <div class="box">
            <div class="box-h">MQTT Broker <span class="info" data-tip="This app connects TO an external MQTT broker (like Mosquitto, HiveMQ, or AWS IoT). It does NOT include its own broker. Configure your broker connection here. Settings are saved to mqtt.json.">i</span><button class="btn" type="button" onclick="openMqttWizard()" style="margin-left:auto">Setup Wizard</button></div>
            <div class="box-b">
                <div class="conn-section">
                    <div class="conn-section-h">Configuration <span class="info" data-tip="Settings saved to mqtt.json. These define HOW the bridge connects to the broker. Changes here do not take effect until you click 'Save Config', and only apply to future connections — they do not connect or disconnect the broker live.">i</span></div>
                    <div class="field"><label class="fl" for="mqttEnabled">Auto-connect</label><span class="info" data-tip="When ON, the bridge connects to the broker automatically on app startup. When OFF, it starts disconnected. To connect or disconnect right now, use the 'Live Connection' buttons below.">i</span><input type="checkbox" id="mqttEnabled"></div>
                    <div class="field"><label class="fl" for="mqttBrokerUrl">Broker URL</label><span class="info" data-tip="Your MQTT broker address. Use tcp:// for plain connection or mqtts:// for encrypted. Example: tcp://192.168.1.100:1883 or mqtts://broker.hivemq.com:8883">i</span><input type="text" id="mqttBrokerUrl" placeholder="tcp://localhost:1883"></div>
                    <div class="field"><label class="fl" for="mqttClientId">Client ID</label><span class="info" data-tip="Unique name for this bridge connection. Your broker uses this to identify the app. Keep the default or change it if running multiple bridges.">i</span><input type="text" id="mqttClientId"></div>
                    <div class="field"><label class="fl" for="mqttUser">Username</label><span class="info" data-tip="Username for broker authentication. Leave empty if your broker doesn't require login.">i</span><input type="text" id="mqttUser"></div>
                    <div class="field"><label class="fl" for="mqttPass">Password</label><span class="info" data-tip="Password for broker authentication. Leave empty if your broker doesn't require login. Stored in mqtt.json file.">i</span><input type="password" id="mqttPass"></div>
                    <div class="field"><label class="fl" for="mqttTls">TLS</label><span class="info" data-tip="Enable encrypted connection to broker. Use this when your broker URL starts with mqtts:// (usually port 8883).">i</span><input type="checkbox" id="mqttTls"></div>
                    <div class="field"><label class="fl" for="mqttIgnoreCert">Ignore Cert</label><span class="info" data-tip="Skip broker certificate check. Only use for testing with self-signed certificates. NOT recommended for production.">i</span><input type="checkbox" id="mqttIgnoreCert"></div>
                    <div class="field"><label class="fl" for="mqttPrefix">Topic Prefix</label><span class="info" data-tip="Prefix for all topics, e.g. bridge/tags. Publish topic = {prefix}/{sourceId}/{itemId}; subscribe filter = {prefix}/#. A per-tag override topic can be set in the tag faceplate.">i</span><input type="text" id="mqttPrefix" placeholder="bridge/tags"></div>
                    <div class="field"><label class="fl" for="mqttFields">Payload Fields</label><span class="info" data-tip="Which fields are included in each published JSON payload. Default {v,t} = value + timestamp. Quality/SourceId/ItemId/DisplayName/DataType add more context.">i</span>
                        <select id="mqttFields">
                            <option>Value, Timestamp</option>
                            <option>Value, Timestamp, Quality</option>
                            <option>Value, Timestamp, Quality, SourceId, ItemId</option>
                            <option>Value, Timestamp, SourceId, ItemId, DisplayName, DataType</option>
                        </select>
                    </div>
                    <div class="field"><button class="btn" onclick="saveMqtt()">Save Config</button><span class="msg">persists to mqtt.json (applies on next connect)</span></div>
                </div>
                <div class="conn-section">
                    <div class="conn-section-h">Live Connection <span class="info" data-tip="Manual control of the broker connection right now. 'Connect' opens a connection using the saved config; 'Disconnect' closes it. These do NOT change the saved 'Auto-connect' setting.">i</span></div>
                    <div class="field">
                        <button class="btn ghost" onclick="connectMqtt()">Connect</button>
                        <button class="btn ghost" onclick="disconnectMqtt()">Disconnect</button>
                        <span class="msg">applies immediately</span>
                    </div>
                </div>
                <div class="msg" id="mqttMessage"></div>
            </div>
        </div>
        <div class="box">
            <div class="box-h">Connection <span class="info" data-tip="Live broker connection status and counters since the last (re)connect.">i</span></div>
            <div class="box-b">
                <div class="stat"><div class="k">State <span class="info" data-tip="Broker connection state: Disconnected, Connecting, Connected, or Faulted (connection failed or dropped).">i</span></div><div class="v" id="mqttState">Disconnected</div><div class="s" id="mqttLastError">No errors</div></div>
                <div class="stat"><div class="k">Published <span class="info" data-tip="Total values published to the broker since the last (re)connect — one per enabled tag update.">i</span></div><div class="v" id="mqttPublished">0</div><div class="s" id="mqttPublishedRate">0.0/s</div></div>
                <div class="stat"><div class="k">Received <span class="info" data-tip="Total inbound messages from the broker since the last (re)connect. Includes the bridge's own publishes echoed back if it subscribes to its own prefix.">i</span></div><div class="v" id="mqttReceived">0</div><div class="s" id="mqttReceivedRate">0.0/s</div></div>
            </div>
        </div>
    </div>
    <div class="modal-overlay" id="mqttWizard" onclick="if(event.target===this)closeMqttWizard()">
  <div class="modal wizard" role="dialog" aria-modal="true" aria-labelledby="mqttWizardTitle">
    <div class="modal-head">
      <div class="modal-title" id="mqttWizardTitle">Connect MQTT Broker</div>
      <button class="modal-close" type="button" onclick="closeMqttWizard()">&times;</button>
    </div>
    <div class="wizard-steps">
      <span class="wizard-step" data-step="1">1. Broker</span>
      <span class="wizard-step" data-step="2">2. Auth &amp; Topics</span>
      <span class="wizard-step" data-step="3">3. Save &amp; Connect</span>
    </div>
    <div class="wizard-body">
      <div class="wizard-pane active" data-pane="1">
        <div class="field"><label class="fl">Broker URL</label><input type="text" id="wzMqttUrl" placeholder="tcp://localhost:1883"></div>
        <div class="field"><label class="fl">Client ID</label><input type="text" id="wzMqttClientId" placeholder="OpcBridge"></div>
        <div class="field"><label class="fl">Auto-connect</label><input type="checkbox" id="wzMqttAuto" checked></div>
      </div>
      <div class="wizard-pane" data-pane="2">
        <div class="field"><label class="fl">Username</label><input type="text" id="wzMqttUser" placeholder="(optional)"></div>
        <div class="field"><label class="fl">Password</label><input type="password" id="wzMqttPass"></div>
        <div class="field"><label class="fl">TLS</label><input type="checkbox" id="wzMqttTls"></div>
        <div class="field"><label class="fl">Topic Prefix</label><input type="text" id="wzMqttPrefix" placeholder="bridge/tags"></div>
        <div class="field"><label class="fl">Payload Fields</label><select id="wzMqttFields"><option>Value, Timestamp</option><option>Value, Timestamp, Quality</option><option>Value, Timestamp, Quality, SourceId, ItemId</option><option>Value, Timestamp, SourceId, ItemId, DisplayName, DataType</option></select></div>
      </div>
      <div class="wizard-pane" data-pane="3">
        <div class="wizard-summary" id="wzMqttSummary"></div>
        <div class="field"><label class="fl">Connect now</label><input type="checkbox" id="wzMqttConnectNow" checked></div>
      </div>
    </div>
    <div class="wizard-foot">
      <button class="btn ghost" type="button" onclick="closeMqttWizard()">Cancel</button>
      <button class="btn ghost" type="button" id="wzMqttBack" onclick="wzMqttStep(-1)">Back</button>
      <button class="btn" type="button" id="wzMqttNext" onclick="wzMqttStep(1)">Next</button>
      <button class="btn" type="button" id="wzMqttFinish" style="display:none" onclick="wzMqttFinish()">Finish</button>
    </div>
  </div>
</div>
</div>
<div class="view" id="view-iot-traffic">
    <div class="box">
        <div class="box-h">Traffic Monitor <span class="info" data-tip="Recent publish (PUB) and subscribe (SUB) messages. PUB = value sent to broker; SUB = inbound message applied via the UA write path.">i</span> <span class="msg" style="margin-left:auto"><button class="btn ghost" onclick="loadMqttValues()">Refresh</button></span></div>
        <div class="box-b">
            <div class="field" style="margin-bottom:10px">
                <label class="fl" for="mqttValDir">Type</label>
                <select id="mqttValDir" onchange="onMqttValFilterChange()">
                    <option value="">All</option>
                    <option value="PUB">PUB</option>
                    <option value="SUB">SUB</option>
                </select>
                <label class="fl" for="mqttValTopic">Topic</label>
                <input id="mqttValTopic" type="text" placeholder="contains…" oninput="onMqttValTopicInput()" style="flex:1;min-width:120px">
                <label class="fl" for="mqttValAuto" style="width:auto">Auto</label>
                <input type="checkbox" id="mqttValAuto" checked onchange="onMqttValFilterChange()">
            </div>
            <div class="list" id="mqttTraffic"><span class="msg">No MQTT tags yet.</span></div>
        </div>
    </div>
</div>
<div class="view" id="view-influx">
    <div class="first-run-banner" id="hintInflux" style="display:none"></div>
    <div class="grid2">
        <div class="box">
            <div class="box-h">Historian <span class="msg" style="font-weight:400;text-transform:none;letter-spacing:0">InfluxDB 2.x/3.x</span> <span class="info" data-tip="This app writes to an external InfluxDB 2.x/3.x server. It does NOT run InfluxDB itself. Configure URL, Org, Bucket and Token here. Settings are saved to influx.json.">i</span><button class="btn" type="button" onclick="openInfluxWizard()" style="margin-left:auto">Setup Wizard</button></div>
            <div class="box-b">
                <div class="conn-section">
                    <div class="conn-section-h">Configuration <span class="info" data-tip="Settings saved to influx.json. Changes take effect after Save Config and apply to the next Connect.">i</span></div>
                    <div class="field"><label class="fl" for="influxEnabled">Auto-connect</label><span class="info" data-tip="When ON, the bridge connects to InfluxDB automatically on app startup. When OFF, it starts disconnected. Use Live Connection buttons to connect or disconnect now.">i</span><input type="checkbox" id="influxEnabled"></div>
                    <div class="field"><label class="fl" for="influxUrl">URL</label><span class="info" data-tip="InfluxDB HTTP API base URL. Example: http://192.168.1.50:8086 or https://us-east-1-1.aws.cloud2.influxdata.com">i</span><input type="text" id="influxUrl" placeholder="http://localhost:8086"></div>
                    <div class="field"><label class="fl" for="influxOrg">Org</label><span class="info" data-tip="InfluxDB organization name (required for 2.x/Cloud).">i</span><input type="text" id="influxOrg" placeholder="my-org"></div>
                    <div class="field"><label class="fl" for="influxBucket">Bucket</label><span class="info" data-tip="Target bucket for written points.">i</span><input type="text" id="influxBucket" placeholder="opc"></div>
                    <div class="field"><label class="fl" for="influxToken">Token</label><span class="info" data-tip="API token with write access to the bucket. Stored in influx.json.">i</span><input type="password" id="influxToken"></div>
                    <div class="field"><label class="fl" for="influxMeasurement">Measurement</label><span class="info" data-tip="Line protocol measurement name. Default opc_tags.">i</span><input type="text" id="influxMeasurement" placeholder="opc_tags"></div>
                    <div class="field"><label class="fl" for="influxTimeoutMs">Timeout ms</label><span class="info" data-tip="HTTP write timeout in milliseconds. Optional; default 5000.">i</span><input type="number" id="influxTimeoutMs" min="100" step="100" value="5000" style="width:100px"></div>
                    <div class="field"><label class="fl" for="influxVerifySsl">Verify SSL</label><span class="info" data-tip="When ON, TLS certificates are validated. Turn OFF only for lab/self-signed endpoints.">i</span><input type="checkbox" id="influxVerifySsl" checked></div>
                    <div class="field"><button class="btn" onclick="saveInflux()">Save Config</button><span class="msg">persists to influx.json (applies on next connect)</span></div>
                </div>
                <div class="conn-section">
                    <div class="conn-section-h">Live Connection <span class="info" data-tip="Manual control of the InfluxDB connection right now. Connect uses the saved config; Disconnect closes the writer. These do NOT change Auto-connect.">i</span></div>
                    <div class="field">
                        <button class="btn ghost" onclick="connectInflux()">Connect</button>
                        <button class="btn ghost" onclick="disconnectInflux()">Disconnect</button>
                        <span class="msg">applies immediately</span>
                    </div>
                </div>
                <div class="msg" id="influxMessage"></div>
            </div>
        </div>
        <div class="box">
            <div class="box-h">Connection <span class="info" data-tip="Live InfluxDB writer status and counters since the last (re)connect.">i</span></div>
            <div class="box-b">
                <div class="stat"><div class="k">State <span class="info" data-tip="Writer connection state: Disconnected, Connecting, Connected, or Faulted.">i</span></div><div class="v" id="influxState">Disconnected</div><div class="s" id="influxLastError">No errors</div></div>
                <div class="stat"><div class="k">Written <span class="info" data-tip="Total points written to InfluxDB since the last (re)connect — one per enabled tag update.">i</span></div><div class="v" id="influxWritten">0</div><div class="s" id="influxWrittenRate">0.0/s</div></div>
            </div>
        </div>
    </div>
<div class="modal-overlay" id="influxWizard" onclick="if(event.target===this)closeInfluxWizard()">
  <div class="modal wizard" role="dialog" aria-modal="true" aria-labelledby="influxWizardTitle">
    <div class="modal-head">
      <div class="modal-title" id="influxWizardTitle">Enable Historian (InfluxDB)</div>
      <button class="modal-close" type="button" onclick="closeInfluxWizard()">&times;</button>
    </div>
    <div class="wizard-steps">
      <span class="wizard-step" data-step="1">1. Server</span>
      <span class="wizard-step" data-step="2">2. Auth</span>
      <span class="wizard-step" data-step="3">3. Save &amp; Connect</span>
    </div>
    <div class="wizard-body">
      <div class="wizard-pane active" data-pane="1">
        <div class="field"><label class="fl">URL</label><input type="text" id="wzInfluxUrl" placeholder="http://localhost:8086"></div>
        <div class="field"><label class="fl">Org</label><input type="text" id="wzInfluxOrg" placeholder="my-org"></div>
        <div class="field"><label class="fl">Bucket</label><input type="text" id="wzInfluxBucket" placeholder="opc"></div>
      </div>
      <div class="wizard-pane" data-pane="2">
        <div class="field"><label class="fl">Token</label><input type="password" id="wzInfluxToken"></div>
        <div class="hint">API token with write access to the bucket. Stored in influx.json.</div>
      </div>
      <div class="wizard-pane" data-pane="3">
        <div class="wizard-summary" id="wzInfluxSummary"></div>
        <div class="field"><label class="fl">Auto-connect</label><input type="checkbox" id="wzInfluxAuto" checked></div>
        <div class="field"><label class="fl">Connect now</label><input type="checkbox" id="wzInfluxConnectNow" checked></div>
      </div>
    </div>
    <div class="wizard-foot">
      <button class="btn ghost" type="button" onclick="closeInfluxWizard()">Cancel</button>
      <button class="btn ghost" type="button" id="wzInfluxBack" onclick="wzInfluxStep(-1)">Back</button>
      <button class="btn" type="button" id="wzInfluxNext" onclick="wzInfluxStep(1)">Next</button>
      <button class="btn" type="button" id="wzInfluxFinish" style="display:none" onclick="wzInfluxFinish()">Finish</button>
    </div>
  </div>
</div>
</div>
<div class="view" id="view-diagram">
    <div class="diag-toolbar">
        <div class="diag-seg" id="diagSeg">
            <span class="seg-pill" id="segPill"></span>
            <button class="diag-tab active" data-diag="all" onclick="showDiagTab('all')">All</button>
            <button class="diag-tab" data-diag="da-ua" onclick="showDiagTab('da-ua')">DA→UA</button>
            <button class="diag-tab" data-diag="interlinks" onclick="showDiagTab('interlinks')">Interlinks</button>
            <button class="diag-tab" data-diag="mqtt" onclick="showDiagTab('mqtt')">MQTT</button>
        </div>
        <div class="diag-zoom" title="Ctrl+wheel zoom toward cursor · drag canvas to pan">
            <button type="button" class="diag-zoom-btn" id="diagZoomOut" title="Zoom out">&minus;</button>
            <span class="diag-zoom-label" id="diagZoomLabel">100%</span>
            <button type="button" class="diag-zoom-btn" id="diagZoomIn" title="Zoom in">+</button>
            <button type="button" class="diag-zoom-btn" id="diagZoomFit" title="Fit entire diagram">Fit</button>
            <button type="button" class="diag-zoom-btn" id="diagZoomFitW" title="Fit width">Fit W</button>
            <button type="button" class="diag-zoom-btn" id="diagZoomReset" title="Reset zoom">Reset</button>
        </div>
        <div class="diag-legend">
            <span class="legend-chip"><span class="legend-dot good"></span>Good</span>
            <span class="legend-chip"><span class="legend-dot warn"></span>Stale</span>
            <span class="legend-chip"><span class="legend-dot bad"></span>Error</span>
            <span class="legend-chip"><span class="legend-dot off"></span>Disabled</span>
        </div>
    </div>
    <div class="diag-canvas" id="diagCanvas">
        <div class="diag-zoom-host" id="diagZoomHost">
            <svg id="diagSvg"></svg>
        </div>
    </div>
</div>
</div>
""";

    public const string Script = """
<script>
const ESC = {'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'};
const esc = s => String(s ?? '').replace(/[&<>'"]/g, c => ESC[c]);
const attr = esc;
let tipEl;
document.addEventListener('mouseover', e => {
    const info = e.target.closest('.info');
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
document.addEventListener('mouseout', e => { if (e.target.closest('.info') && tipEl) tipEl.classList.remove('show'); });
const el = id => document.getElementById(id);
const state = {
    tagPath: '',
    uaBrowseTrail: [],
    interlinkSideSource: { consumer: '', provider: '' },
    sources: [],
    selectedSourceId: 'default',
    editingNewSource: false,
    selectedDriverId: '',
    editingNewDriver: false,
    selectedMxId: '',
    editingNewMx: false,
    addressRangesCache: null,
    editingNewUaSource: false,
    liveValuesEnabled: true,
    liveValuesSource: '',
    lastValueCount: 0,
    updateRateMs: 1000,
    useSubscriptions: true,
    logsLoaded: false,
    appInfoLoaded: false,
    mappings: [],
    interlinks: [],
    interlinkDraft: { consumer: null, provider: null },
    mappingSort: 'name',
    mappingSortDir: 1,
    mappingFilter: '',
    mapType: 'opc-da',
    mqttConfigured: false,
    mqttState: 'Disconnected',
    mqttConnectionState: 'Disconnected',
    influxConfigured: false,
    influxState: 'Disconnected',
    sessionBannerDismissed: false,
    mqttValFilter: { direction: '', topic: '' },
    valuesByKey: new Map(),
    disconnectedKeys: new Set(),
    badQualityKeys: new Set(),
    disconnectedSources: new Set(),
    handleHistory: [],
    handleBaseline: null,
    gdiHistory: [],
    userHistory: [],
    diagramTab: 'all',
    diagramLoaded: false,
    diagramZoom: 1,
    diagramBaseWidth: 1200,
    diagramBaseHeight: 600,
    diagramExpandedSources: {},
    diagramExpandPage: {}
};
const DIAG_ZOOM_MIN = 0.25;
const DIAG_ZOOM_MAX = 3.0;
const DIAG_ZOOM_STEP = 0.1;
const DIAG_EXPAND_PAGE = 80;
let diagPan = null;

function clampDiagramZoom(z) {
    const n = Number(z);
    if (!Number.isFinite(n)) return 1;
    return Math.min(DIAG_ZOOM_MAX, Math.max(DIAG_ZOOM_MIN, Math.round(n * 100) / 100));
}
function applyDiagramZoom() {
    const host = el('diagZoomHost');
    const svg = el('diagSvg');
    const label = el('diagZoomLabel');
    const z = clampDiagramZoom(state.diagramZoom);
    state.diagramZoom = z;
    const w = Math.max(1, Math.round((state.diagramBaseWidth || 1200) * z));
    const h = Math.max(1, Math.round((state.diagramBaseHeight || 600) * z));
    if (svg) {
        svg.setAttribute('width', w);
        svg.setAttribute('height', h);
        svg.style.width = w + 'px';
        svg.style.height = h + 'px';
        svg.style.transform = '';
    }
    if (host) {
        host.style.width = w + 'px';
        host.style.height = h + 'px';
        host.style.transform = '';
    }
    if (label) label.textContent = Math.round(z * 100) + '%';
    const outBtn = el('diagZoomOut');
    const inBtn = el('diagZoomIn');
    if (outBtn) outBtn.disabled = z <= DIAG_ZOOM_MIN + 1e-9;
    if (inBtn) inBtn.disabled = z >= DIAG_ZOOM_MAX - 1e-9;
}
function setDiagramZoom(next, anchor) {
    const canvas = el('diagCanvas');
    const prev = clampDiagramZoom(state.diagramZoom || 1);
    const z = clampDiagramZoom(next);
    let ax = null, ay = null, sx = 0, sy = 0;
    if (canvas && anchor && Number.isFinite(anchor.clientX) && Number.isFinite(anchor.clientY)) {
        const rect = canvas.getBoundingClientRect();
        ax = anchor.clientX - rect.left;
        ay = anchor.clientY - rect.top;
        sx = canvas.scrollLeft;
        sy = canvas.scrollTop;
    }
    state.diagramZoom = z;
    applyDiagramZoom();
    if (canvas && ax !== null && prev > 0) {
        const ratio = z / prev;
        canvas.scrollLeft = Math.max(0, (sx + ax) * ratio - ax);
        canvas.scrollTop = Math.max(0, (sy + ay) * ratio - ay);
    }
}
function nudgeDiagramZoom(delta, anchor) {
    setDiagramZoom((state.diagramZoom || 1) + delta, anchor);
}
function fitDiagramZoom(mode) {
    const canvas = el('diagCanvas');
    if (!canvas) return;
    const baseW = Math.max(1, state.diagramBaseWidth || 1200);
    const baseH = Math.max(1, state.diagramBaseHeight || 600);
    const viewW = Math.max(1, canvas.clientWidth - 16);
    const viewH = Math.max(1, canvas.clientHeight - 16);
    let z = viewW / baseW;
    if (mode !== 'width') z = Math.min(z, viewH / baseH);
    setDiagramZoom(z);
    canvas.scrollLeft = 0;
    canvas.scrollTop = 0;
}
function bindDiagramPanZoom() {
    const canvas = el('diagCanvas');
    if (!canvas || canvas.dataset.zoomBound === '1') return;
    canvas.dataset.zoomBound = '1';
    el('diagZoomIn')?.addEventListener('click', () => nudgeDiagramZoom(DIAG_ZOOM_STEP));
    el('diagZoomOut')?.addEventListener('click', () => nudgeDiagramZoom(-DIAG_ZOOM_STEP));
    el('diagZoomReset')?.addEventListener('click', () => setDiagramZoom(1));
    el('diagZoomFit')?.addEventListener('click', () => fitDiagramZoom('all'));
    el('diagZoomFitW')?.addEventListener('click', () => fitDiagramZoom('width'));
    canvas.addEventListener('wheel', e => {
        if (!e.ctrlKey) return;
        e.preventDefault();
        const delta = e.deltaY > 0 ? -DIAG_ZOOM_STEP : DIAG_ZOOM_STEP;
        nudgeDiagramZoom(delta, { clientX: e.clientX, clientY: e.clientY });
    }, { passive: false });
    canvas.addEventListener('pointerdown', e => {
        if (e.button !== 0) return;
        if (e.target.closest('.diag-node, button, a, input, select, textarea')) return;
        diagPan = { pointerId: e.pointerId, x: e.clientX, y: e.clientY, sl: canvas.scrollLeft, st: canvas.scrollTop };
        canvas.classList.add('panning');
        try { canvas.setPointerCapture(e.pointerId); } catch (_) {}
        e.preventDefault();
    });
    canvas.addEventListener('pointermove', e => {
        if (!diagPan || e.pointerId !== diagPan.pointerId) return;
        canvas.scrollLeft = diagPan.sl - (e.clientX - diagPan.x);
        canvas.scrollTop = diagPan.st - (e.clientY - diagPan.y);
    });
    const endPan = e => {
        if (!diagPan || (e && e.pointerId !== diagPan.pointerId)) return;
        diagPan = null;
        canvas.classList.remove('panning');
    };
    canvas.addEventListener('pointerup', endPan);
    canvas.addEventListener('pointercancel', endPan);
    canvas.addEventListener('click', e => {
        const actionEl = e.target.closest('[data-diag-action]');
        if (!actionEl) return;
        const action = actionEl.dataset.diagAction;
        const sourceId = actionEl.dataset.sourceId || '';
        if (action === 'toggle-expand' && sourceId) {
            const cur = !!state.diagramExpandedSources[sourceId];
            if (cur) delete state.diagramExpandedSources[sourceId];
            else {
                state.diagramExpandedSources[sourceId] = true;
                if (state.diagramExpandPage[sourceId] == null) state.diagramExpandPage[sourceId] = 0;
            }
            renderDiagram();
            return;
        }
        if (action === 'expand-page' && sourceId) {
            const dir = Number(actionEl.dataset.dir || 0);
            const page = Number(state.diagramExpandPage[sourceId] || 0) + dir;
            state.diagramExpandPage[sourceId] = Math.max(0, page);
            state.diagramExpandedSources[sourceId] = true;
            renderDiagram();
        }
    });
    applyDiagramZoom();
}

function syncSegPill() {
    const seg = el('diagSeg');
    const pill = el('segPill');
    const btn = seg ? seg.querySelector('.diag-tab.active') : null;
    if (!seg || !pill || !btn) return;
    pill.style.width = btn.offsetWidth + 'px';
    pill.style.transform = 'translateX(' + btn.offsetLeft + 'px)';
}
window.addEventListener('resize', syncSegPill);

function diagEmptyState(title, hint, w = 1100, h = 600) {
    const cx = Math.round(w / 2), cy = Math.round(h / 2);
    return `<g class="diag-empty" transform="translate(${cx} ${cy})">` +
        `<rect x="-240" y="-62" width="480" height="124" rx="10" fill="url(#diagCardGrad)" stroke="#2a3547" stroke-dasharray="5 5"/>` +
        `<text y="-8" text-anchor="middle" fill="#d8e0ea" font-size="14" font-weight="600">${escapeHtml(title)}</text>` +
        `<text y="16" text-anchor="middle" fill="#6b7689" font-size="11">${escapeHtml(hint)}</text></g>`;
}

const DIAG_DEFS = '<defs>' +
    '<linearGradient id="diagCardGrad" x1="0" y1="0" x2="0" y2="1">' +
    '<stop offset="0" stop-color="#18202e"/><stop offset="1" stop-color="#10151f"/>' +
    '</linearGradient>' +
    '<filter id="diagDrop" x="-20%" y="-20%" width="140%" height="140%">' +
    '<feDropShadow dx="0" dy="2" stdDeviation="3" flood-color="#000000" flood-opacity="0.35"/>' +
    '</filter></defs>';

function showDiagTab(tab) {
    state.diagramTab = tab;
    document.querySelectorAll('.diag-tab').forEach(b => b.classList.toggle('active', b.dataset.diag === tab));
    renderDiagram();
}

function renderDiagram() {
    const svg = document.getElementById('diagSvg');
    if (!svg) return;

    const tab = state.diagramTab || 'all';
    let html = '';
    let maxHeight = 600;
    let maxWidth = 1200;

    if (tab === 'all') {
        const result = renderAllDiagram();
        html = result.svg;
        maxHeight = result.maxHeight;
        maxWidth = result.maxWidth || 1400;
    } else if (tab === 'da-ua') {
        const result = renderDaUaDiagram();
        html = result.svg;
        maxHeight = result.maxHeight;
        maxWidth = result.maxWidth || maxWidth;
    } else if (tab === 'interlinks') {
        const result = renderInterlinksDiagram();
        html = result.svg;
        maxHeight = result.maxHeight;
        maxWidth = result.maxWidth || maxWidth;
    } else if (tab === 'mqtt') {
        const result = renderMqttDiagram();
        html = result.svg;
        maxHeight = result.maxHeight;
        maxWidth = result.maxWidth || maxWidth;
    }

    state.diagramBaseWidth = maxWidth;
    state.diagramBaseHeight = maxHeight;
    svg.setAttribute('viewBox', `0 0 ${maxWidth} ${maxHeight}`);
    svg.innerHTML = DIAG_DEFS + html;
    // Stagger indices drive the entrance cascade (see diagNodeIn/diagEdgeDraw).
    svg.querySelectorAll('.diag-node').forEach((n, i) => n.style.setProperty('--i', i));
    svg.querySelectorAll('.diag-edge').forEach((p, i) => p.style.setProperty('--i', i));
    applyDiagramZoom();
    syncSegPill();
}

function linkEndpoints(link) {
    const providerSourceId = link.providerSourceId || link.ProviderSourceId || 'default';
    const providerItemId = link.providerItemId || link.ProviderItemId || link.providerItemId || link.ProviderItemId || '';
    const consumerSourceId = link.consumerSourceId || link.ConsumerSourceId || link.sourceId || link.SourceId || 'default';
    const consumerItemId = link.consumerItemId || link.ConsumerItemId || link.consumerDaItemId || link.ConsumerDaItemId || link.itemId || link.ItemId || '';
    return {
        providerSourceId,
        providerItemId,
        consumerSourceId,
        consumerItemId,
        providerKey: tagKey(providerSourceId, providerItemId),
        consumerKey: tagKey(consumerSourceId, consumerItemId),
        enabled: (link.enabled ?? link.Enabled) !== false
    };
}

function collectInterlinks() {
    const links = [];
    const seen = new Set();
    const push = (link, kind) => {
        const ep = linkEndpoints(link);
        if (!ep.providerItemId || !ep.consumerItemId) return;
        if (ep.providerKey === ep.consumerKey) return;
        const key = ep.providerKey + '=>' + ep.consumerKey;
        if (seen.has(key)) return;
        seen.add(key);
        links.push({ ...link, ...ep, _kind: kind });
    };
    (state.interlinks || []).forEach(l => push(l, 'rule'));
    // legacy provider fields still present on mappings
    (state.mappings || []).forEach(m => {
        const pSid = m.providerSourceId || m.ProviderSourceId;
        const pItem = m.providerItemId || m.ProviderItemId || m.providerItemId || m.ProviderItemId;
        if (!pSid || !pItem) return;
        push({
            providerSourceId: pSid,
            providerItemId: pItem,
            consumerSourceId: m.sourceId || m.SourceId || 'default',
            consumerItemId: m.itemId || m.ItemId || m.daItemId || m.DaItemId || '',
            enabled: (m.enabled ?? m.Enabled) !== false
        }, 'legacy');
    });
    return links;
}

function tagShortName(tagOrItemId) {
    if (tagOrItemId && typeof tagOrItemId === 'object') {
        const itemId = tagOrItemId.itemId || tagOrItemId.ItemId || tagOrItemId.daItemId || tagOrItemId.DaItemId || '';
        const display = tagOrItemId.displayName || tagOrItemId.DisplayName || '';
        if (display) return String(display);
        return String(itemId).split('.').pop() || itemId || '?';
    }
    return String(tagOrItemId || '').split('.').pop() || String(tagOrItemId || '?');
}

function drawEdge(x1, y1, x2, y2, status, color) {
    return `<path class="diag-edge ${status}" pathLength="1" d="M ${x1} ${y1} L ${x2} ${y2}" stroke="${color}"/>` +
           `<path class="diag-flow ${status}" d="M ${x1} ${y1} L ${x2} ${y2}" stroke="${color}"/>`;
}

function drawCurve(x1, y1, x2, y2, status, color, lift = 40) {
    const midX = (x1 + x2) / 2;
    const midY = Math.min(y1, y2) - lift;
    return `<path class="diag-edge ${status}" pathLength="1" d="M ${x1} ${y1} Q ${midX} ${midY} ${x2} ${y2}" stroke="${color}"/>` +
           `<path class="diag-flow ${status}" d="M ${x1} ${y1} Q ${midX} ${midY} ${x2} ${y2}" stroke="${color}"/>`;
}

function mqttBrokerStatus() {
    const mqttState = (state.mqttConnectionState || el('mqttState')?.textContent || '').toLowerCase();
    if (mqttState.includes('connected')) return 'good';
    if (mqttState.includes('connecting') || mqttState.includes('partial')) return 'warn';
    if (mqttState.includes('fault') || mqttState.includes('error')) return 'bad';
    return 'off';
}

function isMqttEnabled(tag) {
    return (tag.mqttEnabled ?? tag.MqttEnabled) === true;
}

function worstStatus(a, b) {
    const rank = { bad: 3, warn: 2, good: 1, off: 0 };
    return (rank[a] || 0) >= (rank[b] || 0) ? a : b;
}
function summarizeTags(tags) {
    let good = 0, warn = 0, bad = 0, off = 0, mqtt = 0, enabled = 0;
    let flow = 'off';
    (tags || []).forEach(tag => {
        const st = getTagStatus(tag);
        if (st === 'good') good++;
        else if (st === 'warn') warn++;
        else if (st === 'bad') bad++;
        else off++;
        if ((tag.enabled ?? tag.Enabled) !== false) enabled++;
        if (isMqttEnabled(tag)) mqtt++;
        flow = worstStatus(flow, st);
    });
    return { total: (tags || []).length, good, warn, bad, off, mqtt, enabled, flow };
}
function renderAllDiagram() {
    const mappings = state.mappings || [];
    const sources = state.sources || [];
    const links = collectInterlinks();

    if (mappings.length === 0 && sources.length === 0) {
        return { svg: diagEmptyState('No sources or tags configured', 'Add a DA source or mapping to see the plant overview', 1400), maxHeight: 600, maxWidth: 1400 };
    }

    // Aggregated overview: source → tag-group → UA/MQTT (O(sources), not O(tags))
    const sourceX = 40;
    const groupX = 300;
    const uaX = 720;
    const mqttX = 1000;
    const colW = { source: 200, group: 260, hub: 170 };
    const startY = 70;
    const rowH = 84;
    const sourceGap = 22;

    const bySource = new Map();
    mappings.forEach(m => {
        const sid = m.sourceId || m.SourceId || 'default';
        if (!bySource.has(sid)) bySource.set(sid, []);
        bySource.get(sid).push(m);
    });
    sources.forEach(s => {
        const sid = s.sourceId || s.SourceId || 'default';
        if (!bySource.has(sid)) bySource.set(sid, []);
    });

    let svg = '';
    svg += `<text x="40" y="30" fill="#6b7689" font-size="11" font-weight="600">PLANT OVERVIEW (aggregated)</text>`;
    svg += `<text x="40" y="48" fill="#6b7689" font-size="10">Sources → tag groups → UA / MQTT · trunks colored by live status · detail on DA→UA / Interlinks / MQTT tabs</text>`;

    const sourcePositions = new Map();
    const groupPositions = new Map();
    const summaries = new Map();
    let currentY = startY;
    let maxY = startY;
    let totalTags = 0;
    let totalMqtt = 0;
    let overallFlow = 'off';

    Array.from(bySource.entries()).forEach(([sourceId, tags]) => {
        const sourceInfo = sources.find(s => (s.sourceId || s.SourceId) === sourceId);
        const sourceName = sourceInfo?.displayName || sourceInfo?.DisplayName || sourceId;
        const sourceStatus = getSourceStatus(sourceId);
        const sourceColor = getStatusColor(sourceStatus);
        const summary = summarizeTags(tags);
        summaries.set(sourceId, summary);
        totalTags += summary.total;
        totalMqtt += summary.mqtt;
        overallFlow = worstStatus(overallFlow, summary.flow);

        const sourceY = currentY;
        const cy = sourceY + 32;
        sourcePositions.set(sourceId, { x: sourceX, y: sourceY, cy, right: sourceX + colW.source });
        groupPositions.set(sourceId, { x: groupX, y: sourceY, cy, left: groupX, right: groupX + colW.group, cx: groupX + colW.group / 2 });

        svg += `<g class="diag-node" data-source="${escapeHtml(sourceId)}">`;
        svg += `<rect x="${sourceX}" y="${sourceY}" width="${colW.source}" height="64" rx="6" fill="#11161f" stroke="${sourceColor}" stroke-width="2"/>`;
        svg += `<text x="${sourceX + colW.source / 2}" y="${sourceY + 24}" text-anchor="middle" fill="#d8e0ea" font-size="12" font-weight="600">${escapeHtml(sourceName)}</text>`;
        svg += `<text x="${sourceX + colW.source / 2}" y="${sourceY + 44}" text-anchor="middle" fill="#6b7689" font-size="10">${escapeHtml(sourceInfo?.progId || sourceInfo?.ProgId || 'DA source')}</text>`;
        svg += `</g>`;

        const groupStatus = summary.total === 0 ? 'off' : summary.flow;
        const groupColor = getStatusColor(groupStatus);
        const line2 = summary.total === 0
            ? 'no mapped tags'
            : `${summary.good} good · ${summary.warn} stale · ${summary.bad} bad`;
        const line3 = summary.total === 0 ? '' : `${summary.mqtt} MQTT · ${summary.enabled}/${summary.total} enabled`;

        svg += drawEdge(sourceX + colW.source, cy, groupX, cy, groupStatus, groupColor);

        svg += `<g class="diag-node" data-source-group="${escapeHtml(sourceId)}">`;
        svg += `<rect x="${groupX}" y="${sourceY}" width="${colW.group}" height="64" rx="6" fill="#11161f" stroke="${groupColor}" stroke-width="1.5"/>`;
        svg += `<text x="${groupX + colW.group / 2}" y="${sourceY + 20}" text-anchor="middle" fill="#d8e0ea" font-size="12" font-weight="600">${summary.total} tags</text>`;
        svg += `<text x="${groupX + colW.group / 2}" y="${sourceY + 38}" text-anchor="middle" fill="#6b7689" font-size="10">${escapeHtml(line2)}</text>`;
        if (line3) svg += `<text x="${groupX + colW.group / 2}" y="${sourceY + 54}" text-anchor="middle" fill="#6b7689" font-size="10">${escapeHtml(line3)}</text>`;
        svg += `</g>`;

        maxY = Math.max(maxY, sourceY + 64);
        currentY += rowH + sourceGap;
    });

    // Aggregate interlinks by source pair
    const pairMap = new Map();
    links.forEach(link => {
        const ep = linkEndpoints(link);
        const fromSid = ep.providerSourceId || 'default';
        const toSid = ep.consumerSourceId || 'default';
        const key = fromSid + '=>' + toSid;
        if (!pairMap.has(key)) pairMap.set(key, { fromSid, toSid, count: 0, status: 'off', same: fromSid === toSid });
        const row = pairMap.get(key);
        row.count++;
        const st = (link.enabled === false || (link.enabled ?? link.Enabled) === false) ? 'off' : getLinkStatus(link);
        row.status = worstStatus(row.status, st);
    });

    let pairIdx = 0;
    pairMap.forEach(pair => {
        if (pair.same) {
            const g = groupPositions.get(pair.fromSid);
            if (!g) return;
            const color = getStatusColor(pair.status);
            svg += `<circle cx="${g.cx}" cy="${g.y - 6}" r="8" fill="#11161f" stroke="${color}" stroke-width="1.5"/>`;
            svg += `<text x="${g.cx}" y="${g.y - 2}" text-anchor="middle" fill="${color}" font-size="9">${pair.count}</text>`;
            return;
        }
        const from = groupPositions.get(pair.fromSid);
        const to = groupPositions.get(pair.toSid);
        if (!from || !to) return;
        const color = getStatusColor(pair.status);
        const lift = 36 + (pairIdx % 4) * 12;
        pairIdx++;
        svg += drawCurve(from.cx, from.cy, to.cx, to.cy, pair.status, color, lift);
        const midX = (from.cx + to.cx) / 2;
        const midY = Math.min(from.cy, to.cy) - lift + 8;
        svg += `<rect x="${midX - 12}" y="${midY - 9}" width="24" height="14" rx="3" fill="#11161f" stroke="${color}" stroke-width="1"/>`;
        svg += `<text x="${midX}" y="${midY + 2}" text-anchor="middle" fill="${color}" font-size="9">${pair.count}</text>`;
    });

    // UA hub
    const uaStatus = overallFlow;
    const uaColor = getStatusColor(uaStatus);
    const uaY = Math.max(startY, (maxY + startY) / 2 - 28);
    svg += `<g class="diag-node">`;
    svg += `<rect x="${uaX}" y="${uaY}" width="${colW.hub}" height="56" rx="6" fill="#11161f" stroke="${uaColor}" stroke-width="2"/>`;
    svg += `<text x="${uaX + colW.hub / 2}" y="${uaY + 22}" text-anchor="middle" fill="#d8e0ea" font-size="12" font-weight="600">OPC UA Server</text>`;
    svg += `<text x="${uaX + colW.hub / 2}" y="${uaY + 40}" text-anchor="middle" fill="#6b7689" font-size="10">${totalTags} mapped</text>`;
    svg += `</g>`;

    groupPositions.forEach((pos, sourceId) => {
        const summary = summaries.get(sourceId) || { flow: 'off', total: 0 };
        const st = summary.total === 0 ? 'off' : summary.flow;
        svg += drawEdge(pos.right, pos.cy, uaX, uaY + 28, st, getStatusColor(st));
    });

    // MQTT hub
    const brokerStatus = mqttBrokerStatus();
    const brokerColor = getStatusColor(brokerStatus);
    const mqttY = uaY + 100;
    svg += `<g class="diag-node">`;
    svg += `<rect x="${mqttX}" y="${mqttY}" width="${colW.hub}" height="56" rx="6" fill="#11161f" stroke="${brokerColor}" stroke-width="2"/>`;
    svg += `<text x="${mqttX + colW.hub / 2}" y="${mqttY + 22}" text-anchor="middle" fill="#d8e0ea" font-size="12" font-weight="600">MQTT Broker</text>`;
    svg += `<text x="${mqttX + colW.hub / 2}" y="${mqttY + 40}" text-anchor="middle" fill="#6b7689" font-size="10">${totalMqtt}/${totalTags} enabled</text>`;
    svg += `</g>`;

    groupPositions.forEach((pos, sourceId) => {
        const summary = summaries.get(sourceId) || { mqtt: 0, flow: 'off' };
        let edgeStatus = 'off';
        if (summary.mqtt > 0) {
            if (brokerStatus === 'good' && (summary.flow === 'good' || summary.flow === 'warn' || summary.flow === 'bad')) edgeStatus = summary.flow === 'off' ? 'warn' : summary.flow;
            else if (brokerStatus === 'good') edgeStatus = 'warn';
            else edgeStatus = brokerStatus === 'off' ? 'off' : brokerStatus;
        }
        svg += drawEdge(pos.right, pos.cy, mqttX, mqttY + 28, edgeStatus, getStatusColor(edgeStatus));
    });

    svg += `<text x="${sourceX}" y="${Math.max(maxY, mqttY + 56) + 36}" fill="#6b7689" font-size="10">Aggregated trunks · Grey = inactive · Color = live · Curves = DA→DA between sources (count badge)</text>`;

    return { svg, maxHeight: Math.max(maxY, mqttY + 56) + 60, maxWidth: 1240 };
}

function renderDaUaDiagram() {
    const mappings = state.mappings || [];
    const sources = state.sources || [];

    if (mappings.length === 0) {
        return { svg: diagEmptyState('No tags configured', 'Map a tag to watch it flow from its source to UA', 1100), maxHeight: 600, maxWidth: 1100 };
    }

    // Default: aggregated source trunks (scales to tens of thousands).
    // Expand a source to inspect a paged tag slice (DIAG_EXPAND_PAGE).
    const bySource = new Map();
    mappings.forEach(m => {
        const sid = m.sourceId || m.SourceId || 'default';
        if (!bySource.has(sid)) bySource.set(sid, []);
        bySource.get(sid).push(m);
    });
    sources.forEach(s => {
        const sid = s.sourceId || s.SourceId || 'default';
        if (!bySource.has(sid)) bySource.set(sid, []);
    });

    const sourceX = 50;
    const groupX = 300;
    const tagX = 620;
    const uaX = 920;
    const colW = { source: 200, group: 250, tag: 190, hub: 160 };
    const startY = 70;
    const rowH = 84;
    const tagSpacing = 34;
    const sourceGap = 20;
    const pageSize = DIAG_EXPAND_PAGE;

    let svg = '';
    const totalTags = mappings.length;
    const sourceCount = bySource.size;
    svg += `<text x="50" y="28" fill="#6b7689" font-size="11" font-weight="600">Source → UA (aggregated)</text>`;
    svg += `<text x="50" y="46" fill="#6b7689" font-size="10">${sourceCount} sources · ${totalTags} tags · click a tag-group to expand (page ${pageSize}) · Fit/pan for overview</text>`;

    const groupPositions = new Map();
    const summaries = new Map();
    let currentY = startY;
    let maxY = startY;
    let overallFlow = 'off';

    Array.from(bySource.entries()).forEach(([sourceId, tags]) => {
        const sourceInfo = sources.find(s => (s.sourceId || s.SourceId) === sourceId);
        const sourceName = sourceInfo?.displayName || sourceInfo?.DisplayName || sourceId;
        const sourceStatus = getSourceStatus(sourceId);
        const sourceColor = getStatusColor(sourceStatus);
        const summary = summarizeTags(tags);
        summaries.set(sourceId, summary);
        overallFlow = worstStatus(overallFlow, summary.flow);

        const expanded = !!state.diagramExpandedSources[sourceId];
        const page = Math.max(0, Number(state.diagramExpandPage[sourceId] || 0));
        const pageCount = Math.max(1, Math.ceil(Math.max(tags.length, 1) / pageSize));
        const safePage = Math.min(page, pageCount - 1);
        if (safePage !== page) state.diagramExpandPage[sourceId] = safePage;
        const sliceStart = safePage * pageSize;
        const slice = expanded ? tags.slice(sliceStart, sliceStart + pageSize) : [];
        const blockH = expanded
            ? 72 + Math.max(slice.length, 1) * tagSpacing + (tags.length > pageSize ? 30 : 10)
            : 64;
        const sourceY = currentY;
        const groupCy = sourceY + 32;

        // Source box
        svg += `<g class="diag-node" data-source="${escapeHtml(sourceId)}">`;
        svg += `<rect x="${sourceX}" y="${sourceY}" width="${colW.source}" height="64" rx="6" fill="#11161f" stroke="${sourceColor}" stroke-width="2"/>`;
        svg += `<text x="${sourceX + colW.source / 2}" y="${sourceY + 24}" text-anchor="middle" fill="#d8e0ea" font-size="12" font-weight="600">${escapeHtml(sourceName)}</text>`;
        svg += `<text x="${sourceX + colW.source / 2}" y="${sourceY + 44}" text-anchor="middle" fill="#6b7689" font-size="10">${escapeHtml(sourceInfo?.progId || sourceInfo?.ProgId || 'DA source')}</text>`;
        svg += `</g>`;

        // Tag-group summary (click to expand/collapse)
        const groupStatus = summary.total === 0 ? 'off' : summary.flow;
        const groupColor = getStatusColor(groupStatus);
        const line2 = summary.total === 0 ? 'no mapped tags' : `${summary.good} good · ${summary.warn} stale · ${summary.bad} bad`;
        const line3 = expanded ? `expanded · page ${safePage + 1}/${pageCount}` : (summary.total ? 'click to expand tags' : '');

        svg += drawEdge(sourceX + colW.source, groupCy, groupX, groupCy, groupStatus, groupColor);

        svg += `<g class="diag-node" data-diag-action="toggle-expand" data-source-id="${attr(sourceId)}" style="cursor:pointer">`;
        svg += `<rect x="${groupX}" y="${sourceY}" width="${colW.group}" height="64" rx="6" fill="#11161f" stroke="${groupColor}" stroke-width="1.5"/>`;
        svg += `<text x="${groupX + colW.group / 2}" y="${sourceY + 20}" text-anchor="middle" fill="#d8e0ea" font-size="12" font-weight="600">${summary.total} tags ${expanded ? '▾' : '▸'}</text>`;
        svg += `<text x="${groupX + colW.group / 2}" y="${sourceY + 38}" text-anchor="middle" fill="#6b7689" font-size="10">${escapeHtml(line2)}</text>`;
        if (line3) svg += `<text x="${groupX + colW.group / 2}" y="${sourceY + 54}" text-anchor="middle" fill="#6b7689" font-size="10">${escapeHtml(line3)}</text>`;
        svg += `</g>`;

        groupPositions.set(sourceId, {
            x: groupX, y: sourceY, cy: groupCy,
            right: groupX + colW.group,
            expanded, slice, sliceStart, pageCount, safePage, tags
        });

        // Expanded per-tag detail (paged) + direct tag→UA edges
        const detailPositions = [];
        if (expanded && slice.length) {
            slice.forEach((tag, i) => {
                const itemId = tag.itemId || tag.ItemId || tag.daItemId || tag.DaItemId || '';
                const tKey = tagKey(sourceId, itemId);
                const tagY = sourceY + 72 + i * tagSpacing;
                const cy = tagY + 14;
                const tagStatus = getTagStatus(tag);
                const tagColor = getStatusColor(tagStatus);
                const tagName = String(itemId).split('.').pop() || itemId;

                svg += drawEdge(groupX + colW.group, groupCy, tagX, cy, tagStatus, tagColor);
                svg += `<g class="diag-node" data-tag="${escapeHtml(tKey)}">`;
                svg += `<rect x="${tagX}" y="${tagY}" width="${colW.tag}" height="28" rx="4" fill="#11161f" stroke="${tagColor}" stroke-width="1.5"/>`;
                svg += `<text x="${tagX + colW.tag / 2}" y="${tagY + 18}" text-anchor="middle" fill="#d8e0ea" font-size="11">${escapeHtml(tagName)}</text>`;
                svg += `</g>`;
                detailPositions.push({ right: tagX + colW.tag, cy, status: tagStatus, color: tagColor });
                maxY = Math.max(maxY, tagY + 28);
            });

            if (tags.length > pageSize) {
                const navY = sourceY + 72 + slice.length * tagSpacing + 6;
                const canPrev = safePage > 0;
                const canNext = safePage < pageCount - 1;
                if (canPrev) {
                    svg += `<g class="diag-node" data-diag-action="expand-page" data-source-id="${attr(sourceId)}" data-dir="-1" style="cursor:pointer">`;
                    svg += `<rect x="${tagX}" y="${navY}" width="70" height="22" rx="4" fill="#1a2230" stroke="#6b7689"/>`;
                    svg += `<text x="${tagX + 35}" y="${navY + 15}" text-anchor="middle" fill="#d8e0ea" font-size="10">← Prev</text></g>`;
                }
                if (canNext) {
                    svg += `<g class="diag-node" data-diag-action="expand-page" data-source-id="${attr(sourceId)}" data-dir="1" style="cursor:pointer">`;
                    svg += `<rect x="${tagX + 80}" y="${navY}" width="70" height="22" rx="4" fill="#1a2230" stroke="#6b7689"/>`;
                    svg += `<text x="${tagX + 115}" y="${navY + 15}" text-anchor="middle" fill="#d8e0ea" font-size="10">Next →</text></g>`;
                }
                svg += `<text x="${tagX + 170}" y="${navY + 15}" fill="#6b7689" font-size="10">${sliceStart + 1}–${sliceStart + slice.length} / ${tags.length}</text>`;
                maxY = Math.max(maxY, navY + 22);
            }
        }

        groupPositions.get(sourceId).detailPositions = detailPositions;

        maxY = Math.max(maxY, sourceY + blockH);
        currentY += blockH + sourceGap;
    });

    // UA hub
    const uaStatus = overallFlow;
    const uaColor = getStatusColor(uaStatus);
    const uaY = Math.max(startY, (maxY + startY) / 2 - 28);
    svg += `<g class="diag-node">`;
    svg += `<rect x="${uaX}" y="${uaY}" width="${colW.hub}" height="56" rx="6" fill="#11161f" stroke="${uaColor}" stroke-width="2"/>`;
    svg += `<text x="${uaX + colW.hub / 2}" y="${uaY + 22}" text-anchor="middle" fill="#d8e0ea" font-size="12" font-weight="600">OPC UA Server</text>`;
    svg += `<text x="${uaX + colW.hub / 2}" y="${uaY + 40}" text-anchor="middle" fill="#6b7689" font-size="10">${totalTags} mapped</text>`;
    svg += `</g>`;

    // Trunks: collapsed group → UA; expanded visible tags → UA
    groupPositions.forEach((pos, sourceId) => {
        const summary = summaries.get(sourceId) || { flow: 'off', total: 0 };
        const details = pos.detailPositions || [];
        if (pos.expanded && details.length) {
            details.forEach(p => {
                svg += drawEdge(p.right, p.cy, uaX, uaY + 28, p.status, p.color);
            });
            // residual trunk when more tags exist outside the page
            if ((pos.tags || []).length > details.length) {
                const st = summary.flow === 'off' ? 'off' : 'warn';
                svg += drawEdge(pos.right, pos.cy, uaX, uaY + 28, st, getStatusColor(st));
            }
        } else {
            const st = summary.total === 0 ? 'off' : summary.flow;
            svg += drawEdge(pos.right, pos.cy, uaX, uaY + 28, st, getStatusColor(st));
        }
    });

    svg += `<text x="${sourceX}" y="${maxY + 36}" fill="#6b7689" font-size="10">Collapsed = 1 trunk/source (safe at 10k+ tags) · Expanded = paged tag detail · Grey = inactive · Color = live</text>`;

    return { svg, maxHeight: maxY + 60, maxWidth: 1120 };
}

function renderInterlinksDiagram() {
    const mappings = state.mappings || [];
    const sources = state.sources || [];
    const links = collectInterlinks();

    if (mappings.length === 0 && links.length === 0) {
        return { svg: diagEmptyState('No tags or interlinks configured', 'Create an interlink to see providers feed consumers', 1100), maxHeight: 600, maxWidth: 1100 };
    }

    // Aggregated by source pair (scales with links/sources, not every tag).
    // Expand a pair to inspect paged provider→consumer endpoints.
    const pageSize = DIAG_EXPAND_PAGE;
    const startY = 70;
    const leftX = 50;
    const rightX = 620;
    const midX = 360;
    const colW = { source: 220, detail: 200 };
    const rowH = 84;
    const tagSpacing = 34;
    const sourceGap = 20;

    const sourceName = (sid) => {
        const s = (sources || []).find(x => (x.sourceId || x.SourceId) === sid);
        return s?.displayName || s?.DisplayName || sid;
    };

    // Aggregate links by providerSource => consumerSource
    const pairMap = new Map();
    links.forEach(link => {
        const ep = linkEndpoints(link);
        const fromSid = ep.providerSourceId || 'default';
        const toSid = ep.consumerSourceId || 'default';
        const key = fromSid + '=>' + toSid;
        if (!pairMap.has(key)) pairMap.set(key, { key, fromSid, toSid, links: [], status: 'off', same: fromSid === toSid });
        const row = pairMap.get(key);
        row.links.push({ ...link, ...ep });
        const st = (link.enabled === false || (link.enabled ?? link.Enabled) === false) ? 'off' : getLinkStatus(link);
        row.status = worstStatus(row.status, st);
    });

    // Sources involved in links + all mapped sources for empty-state topology
    const involved = new Set();
    pairMap.forEach(p => { involved.add(p.fromSid); involved.add(p.toSid); });
    if (involved.size === 0) {
        (sources || []).forEach(s => involved.add(s.sourceId || s.SourceId || 'default'));
        mappings.forEach(m => involved.add(m.sourceId || m.SourceId || 'default'));
    }

    let svg = '';
    svg += `<text x="50" y="28" fill="#6b7689" font-size="11" font-weight="600">DA TO DA (aggregated)</text>`;
    svg += `<text x="50" y="46" fill="#6b7689" font-size="10">${links.length} link(s) · ${pairMap.size} source-pair(s) · click a pair badge to expand (page ${pageSize})</text>`;

    // Layout provider sources on left, consumer sources on right
    const providers = new Set();
    const consumers = new Set();
    pairMap.forEach(p => { providers.add(p.fromSid); consumers.add(p.toSid); });
    if (providers.size === 0 && consumers.size === 0) {
        Array.from(involved).forEach(sid => providers.add(sid));
    }

    const leftList = Array.from(providers);
    const rightList = Array.from(consumers);
    const leftPos = new Map();
    const rightPos = new Map();
    let y = startY;
    leftList.forEach(sid => {
        leftPos.set(sid, { x: leftX, y, cy: y + 32, right: leftX + colW.source });
        const st = getSourceStatus(sid);
        const color = getStatusColor(st);
        const count = links.filter(l => (l.providerSourceId || 'default') === sid).length;
        svg += `<g class="diag-node" data-source="${escapeHtml(sid)}">`;
        svg += `<rect x="${leftX}" y="${y}" width="${colW.source}" height="64" rx="6" fill="#11161f" stroke="${color}" stroke-width="2"/>`;
        svg += `<text x="${leftX + colW.source / 2}" y="${y + 24}" text-anchor="middle" fill="#d8e0ea" font-size="12" font-weight="600">${escapeHtml(sourceName(sid))}</text>`;
        svg += `<text x="${leftX + colW.source / 2}" y="${y + 44}" text-anchor="middle" fill="#6b7689" font-size="10">provider · ${count} out</text>`;
        svg += `</g>`;
        y += rowH + sourceGap;
    });
    let maxY = y;
    y = startY;
    rightList.forEach(sid => {
        rightPos.set(sid, { x: rightX, y, cy: y + 32, left: rightX });
        const st = getSourceStatus(sid);
        const color = getStatusColor(st);
        const count = links.filter(l => (l.consumerSourceId || 'default') === sid).length;
        svg += `<g class="diag-node" data-source="${escapeHtml(sid)}">`;
        svg += `<rect x="${rightX}" y="${y}" width="${colW.source}" height="64" rx="6" fill="#11161f" stroke="${color}" stroke-width="2"/>`;
        svg += `<text x="${rightX + colW.source / 2}" y="${y + 24}" text-anchor="middle" fill="#d8e0ea" font-size="12" font-weight="600">${escapeHtml(sourceName(sid))}</text>`;
        svg += `<text x="${rightX + colW.source / 2}" y="${y + 44}" text-anchor="middle" fill="#6b7689" font-size="10">consumer · ${count} in</text>`;
        svg += `</g>`;
        y += rowH + sourceGap;
        maxY = Math.max(maxY, y);
    });

    if (pairMap.size === 0) {
        svg += `<text x="50" y="${maxY + 20}" fill="#6b7689" font-size="11">No DA links yet — create provider→consumer links on the Links tab. Sources shown grey until linked/live.</text>`;
        return { svg, maxHeight: maxY + 50, maxWidth: 920 };
    }

    // Draw pair trunks + optional expanded detail under canvas bottom of pair
    let pairIdx = 0;
    let detailY = maxY + 20;
    pairMap.forEach(pair => {
        const from = leftPos.get(pair.fromSid);
        const to = rightPos.get(pair.toSid);
        const color = getStatusColor(pair.status);
        const expandKey = 'dada:' + pair.key;
        const expanded = !!state.diagramExpandedSources[expandKey];
        const page = Math.max(0, Number(state.diagramExpandPage[expandKey] || 0));
        const pageCount = Math.max(1, Math.ceil(Math.max(pair.links.length, 1) / pageSize));
        const safePage = Math.min(page, pageCount - 1);
        if (safePage !== page) state.diagramExpandPage[expandKey] = safePage;
        const sliceStart = safePage * pageSize;
        const slice = expanded ? pair.links.slice(sliceStart, sliceStart + pageSize) : [];

        if (pair.same) {
            // same-source links: badge on left source
            if (from) {
                svg += `<g class="diag-node" data-diag-action="toggle-expand" data-source-id="${attr(expandKey)}" style="cursor:pointer">`;
                svg += `<circle cx="${from.x + colW.source / 2}" cy="${from.y - 8}" r="12" fill="#11161f" stroke="${color}" stroke-width="1.5"/>`;
                svg += `<text x="${from.x + colW.source / 2}" y="${from.y - 4}" text-anchor="middle" fill="${color}" font-size="10">${pair.links.length}</text>`;
                svg += `</g>`;
            }
        } else if (from && to) {
            const lift = 40 + (pairIdx % 5) * 14;
            pairIdx++;
            svg += drawCurve(from.right, from.cy, to.left, to.cy, pair.status, color, lift);
            const badgeX = (from.right + to.left) / 2;
            const badgeY = Math.min(from.cy, to.cy) - lift + 6;
            svg += `<g class="diag-node" data-diag-action="toggle-expand" data-source-id="${attr(expandKey)}" style="cursor:pointer">`;
            svg += `<rect x="${badgeX - 28}" y="${badgeY - 12}" width="56" height="22" rx="4" fill="#11161f" stroke="${color}" stroke-width="1.5"/>`;
            svg += `<text x="${badgeX}" y="${badgeY + 4}" text-anchor="middle" fill="${color}" font-size="10">${pair.links.length}${expanded ? ' ▾' : ' ▸'}</text>`;
            svg += `</g>`;
        }

        if (expanded && slice.length) {
            svg += `<text x="50" y="${detailY + 14}" fill="#6b7689" font-size="11" font-weight="600">${escapeHtml(sourceName(pair.fromSid))} → ${escapeHtml(sourceName(pair.toSid))} · ${sliceStart + 1}–${sliceStart + slice.length} / ${pair.links.length}</text>`;
            detailY += 24;
            slice.forEach((link, i) => {
                const st = (link.enabled === false || (link.enabled ?? link.Enabled) === false) ? 'off' : getLinkStatus(link);
                const c = getStatusColor(st);
                const pLabel = tagShortName(link.providerItemId || '');
                const cLabel = tagShortName(link.consumerItemId || '');
                const kind = link._kind === 'legacy' ? 'legacy' : 'link';
                const rowY = detailY + i * tagSpacing;
                svg += `<g class="diag-node">`;
                svg += `<rect x="50" y="${rowY}" width="${colW.detail}" height="28" rx="4" fill="#11161f" stroke="${c}" stroke-width="1.5"/>`;
                svg += `<text x="${50 + colW.detail / 2}" y="${rowY + 18}" text-anchor="middle" fill="#d8e0ea" font-size="11">${escapeHtml(pLabel)} · P</text>`;
                svg += `</g>`;
                svg += drawEdge(50 + colW.detail, rowY + 14, midX + 40, rowY + 14, st, c);
                svg += `<g class="diag-node">`;
                svg += `<rect x="${midX + 40}" y="${rowY}" width="${colW.detail}" height="28" rx="4" fill="#11161f" stroke="${c}" stroke-width="1.5"/>`;
                svg += `<text x="${midX + 40 + colW.detail / 2}" y="${rowY + 18}" text-anchor="middle" fill="#d8e0ea" font-size="11">${escapeHtml(cLabel)} · C</text>`;
                svg += `</g>`;
                svg += `<text x="${midX + 40 + colW.detail + 12}" y="${rowY + 18}" fill="#6b7689" font-size="10">${escapeHtml(kind)}</text>`;
            });
            detailY += slice.length * tagSpacing + 8;
            if (pair.links.length > pageSize) {
                const canPrev = safePage > 0;
                const canNext = safePage < pageCount - 1;
                if (canPrev) {
                    svg += `<g class="diag-node" data-diag-action="expand-page" data-source-id="${attr(expandKey)}" data-dir="-1" style="cursor:pointer">`;
                    svg += `<rect x="50" y="${detailY}" width="70" height="22" rx="4" fill="#1a2230" stroke="#6b7689"/>`;
                    svg += `<text x="85" y="${detailY + 15}" text-anchor="middle" fill="#d8e0ea" font-size="10">← Prev</text></g>`;
                }
                if (canNext) {
                    svg += `<g class="diag-node" data-diag-action="expand-page" data-source-id="${attr(expandKey)}" data-dir="1" style="cursor:pointer">`;
                    svg += `<rect x="130" y="${detailY}" width="70" height="22" rx="4" fill="#1a2230" stroke="#6b7689"/>`;
                    svg += `<text x="165" y="${detailY + 15}" text-anchor="middle" fill="#d8e0ea" font-size="10">Next →</text></g>`;
                }
                detailY += 30;
            }
            detailY += 16;
            maxY = Math.max(maxY, detailY);
        }
    });

    svg += `<text x="50" y="${maxY + 28}" fill="#6b7689" font-size="10">Pair trunks = aggregated links · click badge to expand paged endpoints · grey = inactive · color = live</text>`;
    return { svg, maxHeight: maxY + 50, maxWidth: 920 };
}

function renderMqttDiagram() {
    const mappings = state.mappings || [];
    const sources = state.sources || [];

    if (mappings.length === 0) {
        return { svg: diagEmptyState('No mapped tags', 'Enable MQTT on a mapped tag to see it published to the broker', 1100), maxHeight: 600, maxWidth: 1100 };
    }

    // Aggregated by DA source → MQTT broker. Expand source for paged tag detail.
    const bySource = new Map();
    mappings.forEach(m => {
        const sid = m.sourceId || m.SourceId || 'default';
        if (!bySource.has(sid)) bySource.set(sid, []);
        bySource.get(sid).push(m);
    });
    sources.forEach(s => {
        const sid = s.sourceId || s.SourceId || 'default';
        if (!bySource.has(sid)) bySource.set(sid, []);
    });

    const sourceX = 50;
    const groupX = 300;
    const tagX = 600;
    const brokerX = 920;
    const colW = { source: 200, group: 250, tag: 200, hub: 170 };
    const startY = 70;
    const tagSpacing = 34;
    const sourceGap = 20;
    const pageSize = DIAG_EXPAND_PAGE;
    const brokerStatus = mqttBrokerStatus();
    const brokerColor = getStatusColor(brokerStatus);
    const totalTags = mappings.length;
    const enabledCount = mappings.filter(isMqttEnabled).length;

    let svg = '';
    svg += `<text x="50" y="28" fill="#6b7689" font-size="11" font-weight="600">MQTT (aggregated)</text>`;
    svg += `<text x="50" y="46" fill="#6b7689" font-size="10">${enabledCount}/${totalTags} MQTT-enabled · ${bySource.size} sources · click group to expand (page ${pageSize}) · broker ${escapeHtml(state.mqttConnectionState || el('mqttState')?.textContent || 'unknown')}</text>`;

    const groupPositions = new Map();
    const summaries = new Map();
    let currentY = startY;
    let maxY = startY;
    let overallMqttFlow = 'off';

    Array.from(bySource.entries()).forEach(([sourceId, tags]) => {
        const sourceInfo = sources.find(s => (s.sourceId || s.SourceId) === sourceId);
        const sourceName = sourceInfo?.displayName || sourceInfo?.DisplayName || sourceId;
        const sourceStatus = getSourceStatus(sourceId);
        const sourceColor = getStatusColor(sourceStatus);
        const summary = summarizeTags(tags);
        summaries.set(sourceId, summary);

        // MQTT-specific flow: only enabled tags contribute color
        let mqttFlow = 'off';
        let mqttLive = 0;
        tags.forEach(t => {
            if (!isMqttEnabled(t)) return;
            const live = getTagStatus(t);
            mqttLive++;
            if (live === 'off') mqttFlow = worstStatus(mqttFlow, brokerStatus === 'good' ? 'warn' : 'off');
            else mqttFlow = worstStatus(mqttFlow, live);
        });
        if (summary.mqtt === 0) mqttFlow = 'off';
        else if (brokerStatus !== 'good' && mqttFlow !== 'off') mqttFlow = brokerStatus === 'off' ? 'off' : worstStatus(mqttFlow, brokerStatus);
        overallMqttFlow = worstStatus(overallMqttFlow, mqttFlow);

        const expandKey = 'mqtt:' + sourceId;
        const expanded = !!state.diagramExpandedSources[expandKey];
        const page = Math.max(0, Number(state.diagramExpandPage[expandKey] || 0));
        const pageCount = Math.max(1, Math.ceil(Math.max(tags.length, 1) / pageSize));
        const safePage = Math.min(page, pageCount - 1);
        if (safePage !== page) state.diagramExpandPage[expandKey] = safePage;
        const sliceStart = safePage * pageSize;
        const slice = expanded ? tags.slice(sliceStart, sliceStart + pageSize) : [];
        const blockH = expanded
            ? 72 + Math.max(slice.length, 1) * tagSpacing + (tags.length > pageSize ? 30 : 10)
            : 64;
        const sourceY = currentY;
        const groupCy = sourceY + 32;

        svg += `<g class="diag-node" data-source="${escapeHtml(sourceId)}">`;
        svg += `<rect x="${sourceX}" y="${sourceY}" width="${colW.source}" height="64" rx="6" fill="#11161f" stroke="${sourceColor}" stroke-width="2"/>`;
        svg += `<text x="${sourceX + colW.source / 2}" y="${sourceY + 24}" text-anchor="middle" fill="#d8e0ea" font-size="12" font-weight="600">${escapeHtml(sourceName)}</text>`;
        svg += `<text x="${sourceX + colW.source / 2}" y="${sourceY + 44}" text-anchor="middle" fill="#6b7689" font-size="10">${escapeHtml(sourceInfo?.progId || sourceInfo?.ProgId || 'DA source')}</text>`;
        svg += `</g>`;

        const groupColor = getStatusColor(mqttFlow);
        const line2 = summary.mqtt === 0 ? 'no MQTT-enabled tags' : `${summary.mqtt} MQTT · ${mqttLive} tracked`;
        const line3 = expanded ? `expanded · page ${safePage + 1}/${pageCount}` : (summary.total ? `${summary.total} mapped · click to expand` : 'no tags');

        svg += drawEdge(sourceX + colW.source, groupCy, groupX, groupCy, mqttFlow, groupColor);

        svg += `<g class="diag-node" data-diag-action="toggle-expand" data-source-id="${attr(expandKey)}" style="cursor:pointer">`;
        svg += `<rect x="${groupX}" y="${sourceY}" width="${colW.group}" height="64" rx="6" fill="#11161f" stroke="${groupColor}" stroke-width="1.5"/>`;
        svg += `<text x="${groupX + colW.group / 2}" y="${sourceY + 20}" text-anchor="middle" fill="#d8e0ea" font-size="12" font-weight="600">${summary.mqtt}/${summary.total} MQTT ${expanded ? '▾' : '▸'}</text>`;
        svg += `<text x="${groupX + colW.group / 2}" y="${sourceY + 38}" text-anchor="middle" fill="#6b7689" font-size="10">${escapeHtml(line2)}</text>`;
        svg += `<text x="${groupX + colW.group / 2}" y="${sourceY + 54}" text-anchor="middle" fill="#6b7689" font-size="10">${escapeHtml(line3)}</text>`;
        svg += `</g>`;

        const detailPositions = [];
        if (expanded && slice.length) {
            slice.forEach((tag, i) => {
                const itemId = tag.itemId || tag.ItemId || tag.daItemId || tag.DaItemId || '';
                const tKey = tagKey(sourceId, itemId);
                const tagY = sourceY + 72 + i * tagSpacing;
                const cy = tagY + 14;
                const mqttOn = isMqttEnabled(tag);
                const live = getTagStatus(tag);
                const nodeStatus = mqttOn ? live : 'off';
                const nodeColor = getStatusColor(nodeStatus);
                const tagName = tagShortName(tag);
                const topic = tag.mqttTopic || tag.MqttTopic || '';

                svg += drawEdge(groupX + colW.group, groupCy, tagX, cy, nodeStatus, nodeColor);
                svg += `<g class="diag-node" data-tag="${escapeHtml(tKey)}">`;
                svg += `<rect x="${tagX}" y="${tagY}" width="${colW.tag}" height="28" rx="4" fill="#11161f" stroke="${nodeColor}" stroke-width="1.5"/>`;
                svg += `<text x="${tagX + colW.tag / 2}" y="${tagY + 12}" text-anchor="middle" fill="#d8e0ea" font-size="11">${escapeHtml(tagName)}</text>`;
                svg += `<text x="${tagX + colW.tag / 2}" y="${tagY + 23}" text-anchor="middle" fill="#6b7689" font-size="9">${mqttOn ? ('ON' + (topic ? ' · ' + escapeHtml(String(topic).slice(0, 18)) : '')) : 'off'}</text>`;
                svg += `</g>`;

                let edgeStatus = 'off';
                if (mqttOn) {
                    if (brokerStatus === 'good' && (live === 'good' || live === 'warn')) edgeStatus = live;
                    else if (brokerStatus === 'good') edgeStatus = 'warn';
                    else edgeStatus = brokerStatus === 'off' ? 'off' : brokerStatus;
                }
                detailPositions.push({ right: tagX + colW.tag, cy, status: edgeStatus, color: getStatusColor(edgeStatus) });
                maxY = Math.max(maxY, tagY + 28);
            });

            if (tags.length > pageSize) {
                const navY = sourceY + 72 + slice.length * tagSpacing + 6;
                const canPrev = safePage > 0;
                const canNext = safePage < pageCount - 1;
                if (canPrev) {
                    svg += `<g class="diag-node" data-diag-action="expand-page" data-source-id="${attr(expandKey)}" data-dir="-1" style="cursor:pointer">`;
                    svg += `<rect x="${tagX}" y="${navY}" width="70" height="22" rx="4" fill="#1a2230" stroke="#6b7689"/>`;
                    svg += `<text x="${tagX + 35}" y="${navY + 15}" text-anchor="middle" fill="#d8e0ea" font-size="10">← Prev</text></g>`;
                }
                if (canNext) {
                    svg += `<g class="diag-node" data-diag-action="expand-page" data-source-id="${attr(expandKey)}" data-dir="1" style="cursor:pointer">`;
                    svg += `<rect x="${tagX + 80}" y="${navY}" width="70" height="22" rx="4" fill="#1a2230" stroke="#6b7689"/>`;
                    svg += `<text x="${tagX + 115}" y="${navY + 15}" text-anchor="middle" fill="#d8e0ea" font-size="10">Next →</text></g>`;
                }
                svg += `<text x="${tagX + 170}" y="${navY + 15}" fill="#6b7689" font-size="10">${sliceStart + 1}–${sliceStart + slice.length} / ${tags.length}</text>`;
                maxY = Math.max(maxY, navY + 22);
            }
        }

        groupPositions.set(sourceId, {
            right: groupX + colW.group,
            cy: groupCy,
            expanded,
            mqttFlow,
            mqttCount: summary.mqtt,
            detailPositions,
            tagCount: tags.length
        });

        maxY = Math.max(maxY, sourceY + blockH);
        currentY += blockH + sourceGap;
    });

    const brokerY = Math.max(startY, (maxY + startY) / 2 - 32);
    svg += `<g class="diag-node">`;
    svg += `<rect x="${brokerX}" y="${brokerY}" width="${colW.hub}" height="64" rx="8" fill="#11161f" stroke="${brokerColor}" stroke-width="2"/>`;
    svg += `<text x="${brokerX + colW.hub / 2}" y="${brokerY + 24}" text-anchor="middle" fill="#d8e0ea" font-size="13" font-weight="600">MQTT Broker</text>`;
    svg += `<text x="${brokerX + colW.hub / 2}" y="${brokerY + 44}" text-anchor="middle" fill="#6b7689" font-size="10">${enabledCount}/${totalTags} enabled</text>`;
    svg += `</g>`;

    groupPositions.forEach(pos => {
        const details = pos.detailPositions || [];
        if (pos.expanded && details.length) {
            details.forEach(p => {
                svg += drawEdge(p.right, p.cy, brokerX, brokerY + 32, p.status, p.color);
            });
            if (pos.tagCount > details.length) {
                const st = pos.mqttFlow === 'off' ? 'off' : 'warn';
                svg += drawEdge(pos.right, pos.cy, brokerX, brokerY + 32, st, getStatusColor(st));
            }
        } else {
            const st = pos.mqttCount === 0 ? 'off' : pos.mqttFlow;
            svg += drawEdge(pos.right, pos.cy, brokerX, brokerY + 32, st, getStatusColor(st));
        }
    });

    svg += `<text x="${sourceX}" y="${Math.max(maxY, brokerY + 64) + 32}" fill="#6b7689" font-size="10">Collapsed = 1 trunk/source · Expanded = paged tags · grey = MQTT off/inactive · color = enabled + live</text>`;
    return { svg, maxHeight: Math.max(maxY, brokerY + 64) + 50, maxWidth: 1140 };
}

function getSourceStatus(sourceId) {
    // Always show topology. Grey when inactive; color only when live/active.
    const source = (state.sources || []).find(s => (s.sourceId || s.SourceId) === sourceId);
    if (!source) return 'off';
    const cs = String(source.connectionState || source.ConnectionState || '').toLowerCase();
    if (cs === 'connected') return 'good';
    if (cs === 'connecting' || cs === 'partial') return 'warn';
    if (cs === 'faulted' || cs === 'error') return 'bad';
    return 'off';
}

function getTagStatus(tag) {
    // Default greyed-out topology. Color only when tag is enabled and live.
    if (!tag || (tag.enabled ?? tag.Enabled) === false) return 'off';

    const sid = tag.sourceId || tag.SourceId || 'default';
    const itemId = tag.itemId || tag.ItemId || tag.daItemId || tag.DaItemId || '';
    const value = state.valuesByKey.get(valueKey(sid, itemId));
    if (!value) return 'off';

    const isGood = value.isGood === true || value.IsGood === true;
    const timestamp = new Date(value.timestampUtc || value.TimestampUtc || 0);
    const age = Date.now() - timestamp.getTime();
    const pollRate = Number(tag.pollRateMs || tag.PollRateMs || 1000) || 1000;

    if (!isGood) return 'bad';
    if (!Number.isFinite(age) || age > pollRate * 2) return 'warn';
    return 'good';
}

function getLinkStatus(link) {
    const ep = linkEndpoints(link);
    if ((link.enabled ?? link.Enabled) === false) return 'off';
    const provider = (state.mappings || []).find(m =>
        tagKey(m.sourceId || m.SourceId || 'default', m.itemId || m.ItemId || m.daItemId || m.DaItemId || '') === ep.providerKey);
    if (!provider) return 'off';
    return getTagStatus(provider);
}

function getStatusColor(status) {
    const colors = {
        good: '#34d399',
        warn: '#fbbf24',
        bad: '#f87171',
        off: '#6b7689'
    };
    return colors[status] || colors.off;
}

function escapeHtml(text) {
    return String(text).replace(/[&<>"']/g, c => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#39;'
    }[c]));
}

function valueKey(sourceId, itemId) {
    return (sourceId || 'default') + '\u0000' + (itemId || '');
}
function tagKey(sourceId, itemId) {
    return (sourceId || 'default') + '||' + (itemId || '');
}
function parseTagKey(key) {
    const idx = key.indexOf('||');
    if (idx < 0) return [key, ''];
    return [key.substring(0, idx), key.substring(idx + 2)];
}

function currentValue(sourceId, itemId) {
    return state.valuesByKey.get(valueKey(sourceId, itemId)) || null;
}

function renderLiveValue(value, fallbackType) {
    if (!value) return '<span class="msg">No live value</span>';
    const text = String(get(value, 'value') ?? '');
    const quality = get(value, 'daQuality');
    const isGood = !!get(value, 'isGood');
    const timestamp = locTime(get(value, 'timestampUtc'));
    const type = get(value, 'dataType') || fallbackType || '—';
    return `<div class="fp-v mono" title="${attr(text)}">${esc(text)}</div><div class="fp-meta"><span class="pill" style="padding:1px 6px;font-size:10px" title="Data type">${esc(type)}</span><span>${badge(isGood ? 'Good' : 'Bad', isGood ? 'good' : 'bad')} <span class="${isGood ? 'good' : 'bad'}">(${esc(String(quality ?? '—'))})</span></span><span class="timestamp">${esc(timestamp)}</span></div>`;
}

function linkTagLabel(sourceId, itemId, nameOverride = null) {
    const mapping = getMapping(sourceId, itemId);
    const name = nameOverride || (mapping ? (mapping.displayName || mapping.DisplayName || itemId) : itemId);
    return `${name} (${sourceId || 'default'} · ${itemId})`;
}
function isLinkableInterlinkSource(source) {
    const t = String(get(source, 'sourceType') || 'OpcDa');
    return t === 'OpcDa' || t === 'OpcUa' || t === 'MxComponent';
}
function interlinkSideIds() { return ['consumer', 'provider']; }
function interlinkSourceSelectId(side) { return side === 'consumer' ? 'interlinkConsumerSource' : 'interlinkProviderSource'; }
function interlinkListId(side) { return side === 'consumer' ? 'interlinkConsumerList' : 'interlinkProviderList'; }
function renderInterlinkPickers() {
    const sources = (state.sources || []).filter(isLinkableInterlinkSource);
    interlinkSideIds().forEach(side => {
        const sel = el(interlinkSourceSelectId(side));
        if (!sel) return;
        const current = state.interlinkSideSource[side] || '';
        sel.innerHTML = '<option value="">— select source —</option>' + sources.map(s =>
            `<option value="${attr(s.sourceId)}"${s.sourceId === current ? ' selected' : ''}>${esc(s.displayName || s.sourceId)} (${esc(sourceTypeLabel(s))})</option>`).join('');
        renderInterlinkTagList(side);
    });
}
function onInterlinkSourceChange(side) {
    const sel = el(interlinkSourceSelectId(side));
    if (!sel) return;
    state.interlinkSideSource[side] = sel.value || '';
    renderInterlinkTagList(side);
}
function renderInterlinkTagList(side) {
    const listEl = el(interlinkListId(side));
    if (!listEl) return;
    const sid = state.interlinkSideSource[side];
    if (!sid) {
        listEl.innerHTML = '<span class="msg">Select a source to list its Maps tags.</span>';
        return;
    }
    const rows = (state.mappings || [])
        .filter(m => String(m.sourceId || m.SourceId || 'default') === sid && (m.enabled ?? m.Enabled) !== false)
        .map(m => {
            const item = m.itemId || m.ItemId || m.daItemId || m.DaItemId || '';
            const name = m.displayName || m.DisplayName || item;
            const key = tagKey(sid, item);
            const picked = state.interlinkDraft[side] && state.interlinkDraft[side].key === key;
            return `<div class="li"><div style="flex:1;min-width:0"><div class="n">${esc(name)}</div><div class="p">${esc(item)}</div></div><button class="btn ghost" data-action="pick-interlink-${side}" data-source-id="${attr(sid)}" data-item-id="${attr(item)}" data-name="${attr(name)}">${picked ? '✓ Picked' : 'Pick'}</button></div>`;
        });
    listEl.innerHTML = rows.length ? rows.join('') : '<span class="msg">No Maps tags for this source yet — add tags on the Maps tab first.</span>';
}
function setInterlinkSelection(role, sourceId, itemId, name) {
    state.interlinkDraft[role] = {
        key: tagKey(sourceId, itemId),
        sourceId: sourceId || 'default',
        itemId,
        name: name || itemId
    };
    const roleName = role === 'consumer' ? 'Consumer' : 'Provider';
    el('linksMessage').textContent = roleName + ' selected from source ' + (sourceId || 'default') + '.';
    renderInterlinksView();
}
function renderInterlinksView() {
    const links = state.interlinks || [];
    const consumer = state.interlinkDraft.consumer;
    const provider = state.interlinkDraft.provider;
    renderInterlinkPickers();
    el('btnSetLink').disabled = !(consumer && provider);
    el('btnClearLink').disabled = !(consumer && findInterlinkByConsumer(consumer.key));
    el('btnClearLinkSelection').disabled = !(consumer || provider);
    el('linksCount').textContent = links.length ? links.length + (links.length === 1 ? ' rule' : ' rules') : 'No rules';
    el('linksList').innerHTML = links.length ? links.map(link => {
        const consumerSourceId = link.consumerSourceId || link.ConsumerSourceId || 'default';
        const consumerItemId = link.consumerItemId || link.ConsumerItemId || '';
        const providerSourceId = link.providerSourceId || link.ProviderSourceId || 'default';
        const providerItemId = link.providerItemId || link.ProviderItemId || '';
        const linkId = link.id || link.Id || '';
        return `<div class="li"><div style="flex:1;min-width:0"><span class="n">${esc(linkTagLabel(consumerSourceId, consumerItemId))}</span></div><span class="pill" style="padding:1px 6px;font-size:10px;background:#e8f0fe;color:#1a73e8">⇠ fed by</span><div style="flex:1;min-width:0"><span class="n">${esc(linkTagLabel(providerSourceId, providerItemId))}</span></div><button class="btn ghost" type="button" data-action="unlink" data-link-id="${attr(linkId)}">Delete</button></div>`;
    }).join('') : '<span class="msg">No interlinks yet. Pick a consumer and a provider above, then Save Link.</span>';
}
function findInterlinkByConsumer(consumerKey) {
    return (state.interlinks || []).find(link => tagKey(link.consumerSourceId || link.ConsumerSourceId || 'default', link.consumerItemId || link.ConsumerItemId || '') === consumerKey) || null;
}
async function saveInterlink(consumerKey, providerKey) {
    if (!consumerKey || !providerKey) { el('linksMessage').textContent = 'Pick both a consumer and a provider.'; return; }
    if (consumerKey === providerKey) { el('linksMessage').textContent = '✗ A tag cannot link to itself.'; return; }
    const [consumerSourceId, consumerItemId] = parseTagKey(consumerKey);
    const [providerSourceId, providerItemId] = parseTagKey(providerKey);
    const existing = findInterlinkByConsumer(consumerKey);
    const link = {
        id: existing ? (existing.id || existing.Id) : '00000000-0000-0000-0000-000000000000',
        providerSourceId: providerSourceId || 'default',
        providerItemId,
        consumerSourceId: consumerSourceId || 'default',
        consumerItemId,
        enabled: existing ? ((existing.enabled ?? existing.Enabled) !== false) : true
    };
    const url = existing ? '/api/interlinks/' + encodeURIComponent(link.id) : '/api/interlinks';
    const method = existing ? 'PUT' : 'POST';
    const r = await fetch(url, {
        method,
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ link })
    });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    el('linksMessage').textContent = existing ? '✓ Interlink updated.' : '✓ Interlink created.';
    await loadInterlinks();
}
async function deleteInterlink(linkId) {
    if (!linkId) { el('linksMessage').textContent = 'Pick a saved interlink to delete.'; return; }
    const r = await fetch('/api/interlinks/' + encodeURIComponent(linkId), { method: 'DELETE' });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    el('linksMessage').textContent = '✓ Interlink removed.';
    await loadInterlinks();
}
function clearInterlinkDraftSelection() {
    state.interlinkDraft.consumer = null;
    state.interlinkDraft.provider = null;
    el('linksMessage').textContent = 'Selection cleared.';
    renderInterlinksView();
}
function renderMappingRow(mapping) {
    const sourceId = mapping.sourceId || mapping.SourceId || 'default';
    const item = mapping.itemId || mapping.ItemId || mapping.daItemId || mapping.DaItemId;
    const name = mapping.displayName || mapping.DisplayName || item;
    const node = mapping.uaNodeId || mapping.UaNodeId || defaultUaNodeId(sourceId, item);
    const mode = mapping.mode || mapping.Mode || 'Source';
    const enabled = (mapping.enabled ?? mapping.Enabled) !== false;
    const writeable = (mapping.writeable ?? mapping.Writeable) === true;
    const pollRate = mapping.pollRateMs ?? mapping.PollRateMs ?? 0;
    const deadband = Number(mapping.deadbandPct ?? mapping.DeadbandPct ?? 0);
    const access = deriveAccess(mapping);
    const simulated = (mode === 'Manual');
    let accessBadge;
    if (!enabled) { accessBadge = badge('Disabled', 'bad'); }
    else { accessBadge = badge(access + (simulated && access !== 'Write' ? ' / Sim' : ''), access === 'Read' ? 'good' : access === 'Read-Write' ? 'partial' : 'warn'); }
    const rateBadge = pollRate > 0 ? `<span class="pill" style="padding:1px 6px;font-size:10px">${pollRate}ms</span>` : '';
    const subName = String(mapping.subscription ?? mapping.Subscription ?? '').trim();
    const subBadge = subName ? `<span class="pill" style="padding:1px 6px;font-size:10px" title="UA subscription">${esc(subName)}</span>` : '';
    const deadbandBadge = deadband > 0 ? `<span class="pill" style="padding:1px 6px;font-size:10px">db ${deadband}%</span>` : '';
    const mqttOn = (mapping.mqttEnabled ?? mapping.MqttEnabled) === true;
    const mqttBadge = mqttOn ? `<span class="pill" style="padding:1px 6px;font-size:10px">MQTT</span>` : '';
    const influxOn = (mapping.influxEnabled ?? mapping.InfluxEnabled) === true;
    const influxBadge = influxOn ? `<span class="pill" style="padding:1px 6px;font-size:10px">Influx</span>` : '';
    // Runtime type from the live value when present (matches Live Values); otherwise the configured type.
    const live = currentValue(sourceId, item);
    const mappedType = (live && get(live, 'dataType')) || mapping.dataType || mapping.DataType || '—';
    const typeBadge = `<span class="pill" style="padding:1px 6px;font-size:10px" title="Data type">${esc(mappedType)}</span>`;
    // Connection state comes from server-side signals, never from absence in the capped
    // value window: the bridge reports tags whose monitored item failed (auto-retrying),
    // tags whose last value is bad quality, and the per-source connection state.
    const sourceDown = enabled && state.disconnectedSources.has(sourceId);
    const failedItem = enabled && state.disconnectedKeys.has(valueKey(sourceId, item));
    const badQuality = enabled && state.badQualityKeys.has(valueKey(sourceId, item));
    let discBadge = '';
    let discTitle = '';
    if (sourceDown) { discBadge = badge('Disc', 'bad'); discTitle = 'Disconnected — source is not connected'; }
    else if (failedItem) { discBadge = badge('Disc', 'bad'); discTitle = 'Disconnected — no value received (auto-retrying)'; }
    else if (badQuality) { discBadge = badge('Bad', 'bad'); discTitle = 'Bad quality from source'; }
    // Full status summary — clipped badges stay discoverable via the row tooltip.
    const statusSummary = [mappedType + ' type', deadband > 0 ? 'db ' + deadband + '%' : null, pollRate > 0 ? pollRate + 'ms' : null, subName ? 'sub ' + subName : null, mqttOn ? 'MQTT' : null, influxOn ? 'Influx' : null, sourceDown ? 'Source disconnected' : null, failedItem ? 'Disconnected (auto-retrying)' : null, badQuality ? 'Bad quality' : null, access + (simulated && access !== 'Write' ? ' / Sim' : '')].filter(Boolean).join(' · ');
    const desc = (mapping.description || mapping.Description || '').trim();
    const descIcon = desc ? `<span class="li-desc" title="${attr(desc)}" data-action="open-faceplate" data-source-id="${attr(sourceId)}" data-item-id="${attr(item)}">&#8505;</span>` : '';
    // Config badges clip/fade first; the colored access status is pinned at the far
    // right and never gets cut off.
    return `<div class="li clickable" data-action="open-faceplate" data-source-id="${attr(sourceId)}" data-item-id="${attr(item)}">${descIcon}<div style="flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap"><span class="n">${esc(name)}</span> <span class="p">${esc(sourceId)} · ${esc(item)} · UA: ${esc(node)}</span></div><div class="li-badge" title="${attr(statusSummary)}"><span class="li-badge-clip">${typeBadge}${deadbandBadge}${rateBadge}${subBadge}${mqttBadge}${influxBadge}</span><span class="li-badge-status">${discBadge ? `<span title="${attr(discTitle)}">${discBadge}</span>` : ''}${accessBadge}</span></div></div>`;
}

const MAPPING_ROWS_CAP = 1000;
function renderMappingRows(mappings) {
    const rows = mappings.length > MAPPING_ROWS_CAP ? mappings.slice(0, MAPPING_ROWS_CAP) : mappings;
    const note = mappings.length > MAPPING_ROWS_CAP
        ? `<span class="msg">… showing first ${MAPPING_ROWS_CAP} of ${mappings.length} mappings — use the search box to filter</span>`
        : '';
    return (rows.length ? rows.map(renderMappingRow).join('') : '<span class="msg">No source → OPC UA mappings.</span>') + note;
}

let faceplateOpen = false;
let faceplateKey = null;

function openFaceplate(sourceId, itemId) {
    const mapping = getMapping(sourceId, itemId);
    if (!mapping) return;
    faceplateOpen = true;
    faceplateKey = valueKey(sourceId, itemId);
    const name = mapping.displayName || mapping.DisplayName || itemId;
    const node = mapping.uaNodeId || mapping.UaNodeId || defaultUaNodeId(sourceId, itemId);
    const mode = mapping.mode || mapping.Mode || 'Source';
    const access = mapping.accessRights || mapping.AccessRights || 'Read';
    const simulated = (mode === 'Manual');
    const enabled = (mapping.enabled ?? mapping.Enabled) !== false;
    const manualValue = mapping.manualValue ?? mapping.ManualValue ?? '';
    el('fpName').textContent = name;
    el('fpSub').textContent = sourceId + ' · ' + itemId + ' · UA: ' + node;
    el('fpDisplayName').value = name;
    el('fpDaItemId').value = itemId;
    el('fpUaNodeId').value = node;
    el('fpDescription').value = String(mapping.description ?? mapping.Description ?? '');
    el('fpAccess').value = access;
    el('fpEnabled').checked = enabled;
    el('fpSimulated').checked = simulated;
    el('fpManualInput').value = String(manualValue ?? '');
    const pollRate = mapping.pollRateMs ?? mapping.PollRateMs ?? 0;
    const mapDaGroup = String(mapping.daGroup ?? mapping.DaGroup ?? '');
    {
        const sel = el('fpPollRate');
        if (sel) sel.innerHTML = fpRateOptions(sourceId, mapDaGroup, pollRate);
    }
    ensureDaGroupsCache(sourceId).then(() => {
        const sel = el('fpPollRate');
        if (sel && document.activeElement !== sel) sel.innerHTML = fpRateOptions(sourceId, mapDaGroup, pollRate);
    }).catch(() => {});
    // SUBSCRIPTION selector (OPC UA sources only): named subs from uaSubsCache.
    // A non-empty choice locks the per-tag rate input — rate comes from the subscription.
    const subApplies = isUaSource(state.sources.find(s => s.sourceId === sourceId) || null);
    const fpSubField = el('fpSubscriptionField');
    const fpSubSel = el('fpSubscription');
    if (fpSubField && fpSubSel) {
        fpSubField.style.display = subApplies ? '' : 'none';
        const fpSubVal = String(mapping.subscription ?? mapping.Subscription ?? '');
        fpSubSel.innerHTML = fpSubscriptionOptions(sourceId, fpSubVal);
        updateFpRateEnabled();
        if (subApplies) {
            loadUaSubs().then(() => {
                if (!faceplateOpen || faceplateKey !== valueKey(sourceId, itemId)) return;
                const sel2 = el('fpSubscription');
                if (sel2 && document.activeElement !== sel2) sel2.innerHTML = fpSubscriptionOptions(sourceId, sel2.value || fpSubVal);
                updateFpRateEnabled();
            }).catch(() => {});
        }
    }
    const deadband = Number(mapping.deadbandPct ?? mapping.DeadbandPct ?? 0);
    el('fpDeadband').value = String(deadband);
    el('fpMqttEnabled').checked = (mapping.mqttEnabled ?? mapping.MqttEnabled) === true;
    el('fpMqttTopic').value = String(mapping.mqttTopic ?? mapping.MqttTopic ?? '');
    el('fpInfluxEnabled').checked = (mapping.influxEnabled ?? mapping.InfluxEnabled) === true;
    updateManualInputState();
    el('fpApply').dataset.sourceId = sourceId;
    el('fpApply').dataset.itemId = itemId;
    el('fpRemove').dataset.sourceId = sourceId;
    el('fpRemove').dataset.itemId = itemId;
    el('fpEnabled').dataset.sourceId = sourceId;
    el('fpEnabled').dataset.itemId = itemId;
    el('fpLivePanel').innerHTML = renderLiveValue(currentValue(sourceId, itemId), mapping.dataType || mapping.DataType || null);
    el('faceplateOverlay').classList.add('open');
}
function deriveAccess(mapping) {
    const access = mapping.accessRights || mapping.AccessRights;
    if (access) return access;
    // Legacy fallback
    const mode = mapping.mode || mapping.Mode || 'Source';
    const writeable = (mapping.writeable ?? mapping.Writeable) === true;
    if (mode === 'Manual' && writeable) return 'Write';
    if (writeable) return 'Read-Write';
    return 'Read';
}
function showFpTab(name) {
    document.querySelectorAll('.fp-subtab').forEach(b => b.classList.toggle('active', b.dataset.fptab === name));
    document.querySelectorAll('.fp-tabpane').forEach(p => p.style.display = p.id === 'fp-pane-' + name ? 'flex' : 'none');
}

function closeFaceplate() {
    faceplateOpen = false;
    faceplateKey = null;
    el('faceplateOverlay').classList.remove('open');
}

function updateFaceplateLiveValues() {
    if (!faceplateOpen || !faceplateKey) return;
    const parts = faceplateKey.split('\u0000');
    el('fpLivePanel').innerHTML = renderLiveValue(currentValue(parts[0] || 'default', parts[1] || ''));
}

function updateManualInputState() {
    const simCheck = el('fpSimulated');
    const manualInput = el('fpManualInput');
    if (!simCheck || !manualInput) return;
    manualInput.disabled = !simCheck.checked;
    el('fpModeHint').textContent = simCheck.checked
        ? 'Simulation ON: bridge publishes the Manual Value to UA instead of reading from DA.'
        : 'Simulation OFF: bridge reads from DA (for Read/Read-Write). Toggle to inject a fixed value.';
}

const ROUTE_TO_TAB = {
  'connectivity/sources': 'connection',
  'connectivity/opc-da': 'opc-da',
  'connectivity/opc-da-groups': 'opc-da-groups',
  'connectivity/opc-ua': 'opc-ua',
  'connectivity/ua-subs': 'ua-subs',
  'connectivity/drivers': 'drivers',
  'connectivity/mx-component': 'mx-component',
  'ops/diagnostics': 'diagnostics',
  'connectivity/diagnostics': 'diagnostics',
  'ops/sessions': 'sessions',
  'tags/maps': 'tags',
  'tags/maps/opc-da': 'tags',
  'tags/maps/opc-ua': 'tags',
  'tags/maps/drivers': 'tags',
  'tags/maps/mx': 'tags',
  'tags/interlinks': 'interlinks',
  'tags/links': 'interlinks', // bookmark alias
  'iot/mqtt': 'mqtt',
  'iot/traffic': 'iot-traffic',
  'historian/influx': 'influx',
  'ops/monitor': 'monitor',
  'ops/values': 'values',
  'ops/logs': 'logs',
  'ops/diagram': 'diagram',
  'help/guide': 'help',
  'help/about': 'about'
};
const DEFAULT_ROUTE = 'ops/monitor';

async function navigate(route) {
  if (route === 'tags/maps') route = 'tags/maps/' + (state.mapType || 'opc-da');
  else if (route === 'tags/maps/opc-da' || route === 'tags/maps/opc-ua' || route === 'tags/maps/drivers' || route === 'tags/maps/mx') {
    state.mapType = route.slice('tags/maps/'.length);
  }
  const tab = ROUTE_TO_TAB[route] || ROUTE_TO_TAB[DEFAULT_ROUTE];
  await showTab(tab, route);
}

async function showTab(name, route) {
  route = route || (Object.keys(ROUTE_TO_TAB).find(r => ROUTE_TO_TAB[r] === name) || DEFAULT_ROUTE);
  const activeTab = name;
  const mapsActive = activeTab === 'tags' || (route && String(route).startsWith('tags/maps'));
  document.querySelectorAll('.tabbtn').forEach(b => {
    const br = b.dataset.route || '';
    b.classList.toggle('active', br === route || (mapsActive && br === 'tags/maps'));
  });
  document.querySelectorAll('.view').forEach(v => v.classList.toggle('active', v.id === 'view-' + activeTab));
  if (location.hash !== '#/' + route) history.replaceState(null, '', '#/' + route);
  if (activeTab === 'logs') { state.logsLoaded = false; loadLogs(true).catch(e => el('logMessage').textContent = '✗ ' + e.message); }
  if (activeTab === 'diagnostics' || activeTab === 'sessions') { diagnosticsActive = true; loadDiagnostics(); }
  else { diagnosticsActive = false; }
  if (activeTab === 'about') loadAppInfo().catch(e => el('aboutName').textContent = '✗ ' + e.message);
  if (activeTab === 'help') loadHelp().catch(e => el('helpContent').innerHTML = '<span class="msg bad">✗ ' + esc(e.message) + '</span>');
  if (activeTab === 'mx-component') { renderMx(); }
  if (activeTab === 'mqtt') { await loadMqtt(); }
  if (activeTab === 'iot-traffic') { await loadMqttValues(); }
  if (name === 'influx') { await loadInflux(); }
  if (activeTab === 'opc-da' || activeTab === 'opc-ua' || activeTab === 'connection') {
    await loadSources().catch(e => console.warn(e));
  }
  if (activeTab === 'ua-subs') {
    await loadUaSubs().catch(e => el('subsMsg').textContent = '✗ ' + e.message);
  }
  if (activeTab === 'drivers') {
    await loadSources().catch(e => console.warn(e));
    renderDrivers();
  }
  if (activeTab === 'opc-da-groups') {
     await loadSources().catch(e => console.warn(e));
     await loadDaGroupsTab().catch(e => console.warn(e));
   }
  if (activeTab === 'tags') {
    await loadSources().catch(e => console.warn(e));
    await loadMappings().catch(e => console.warn(e));
    syncMapTypeUi();
    ensureMapSourceSelection();
    renderMapSourceSelect();
    updateMapEmptyBanner();
    updateMapBrowseUi();
    rerenderMappings();
  }
  if (activeTab === 'diagram') {
    state.diagramLoaded = true;
    await Promise.all([loadSources(), loadMappings(), loadInterlinks(), loadMqtt().catch(() => {})]);
    renderDiagram();
  }
}
function badge(t, c) { return `<span class="badge ${c}">${esc(t)}</span>`; }
function stateClass(v) {
    if (!v) return 'warn';
    const s = String(v).toLowerCase();
    if (s === 'running' || s === 'connected') return 'good';
    if (s === 'partial') return 'partial';
    if (s === 'faulted' || s === 'stopped' || s === 'disconnected') return 'bad';
    return 'warn';
}
function relTime(u) {
    if (!u) return '—';
    const d = Math.floor((Date.now() - new Date(u)) / 1000);
    if (d < 5) return 'just now';
    if (d < 60) return d + 's ago';
    if (d < 3600) return Math.floor(d / 60) + 'm ago';
    return new Date(u).toLocaleTimeString();
}
function shortTime(u) {
    if (!u) return '—';
    return new Date(u).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3 });
}
function locTime(u) {
    if (!u) return '—';
    return new Date(u).toLocaleString([], { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3 });
}
function get(o, k) { return o?.[k] ?? o?.[k[0].toUpperCase() + k.slice(1)]; }
function currentSource() { return state.editingNewSource ? null : state.sources.find(s => s.sourceId === state.selectedSourceId) || null; }
function defaultUaNodeId(sourceId, itemId) { return `ns=2;s=${sourceId}/${itemId}`; }
function isUaSource(source) {
    const t = String(source?.sourceType || source?.SourceType || 'OpcDa');
    return t.toLowerCase() === 'opcua';
}
function isMelsecSource(source) { return String(get(source, 'sourceType') || 'OpcDa') === 'MelsecA3n'; }
function isS7Source(source) { return String(get(source, 'sourceType') || 'OpcDa') === 'S7200Ppi'; }
function isMxSource(source) { return String(get(source, 'sourceType') || 'OpcDa') === 'MxComponent'; }
// Serial PLC drivers (configured in-app). MX Component sources live on their own tab.
function isDriverSource(source) { return isMelsecSource(source) || isS7Source(source); }
function opcDaSources() { return state.sources.filter(s => !isUaSource(s) && !isDriverSource(s) && !isMxSource(s)); }
function mapTypeSources(type) {
    type = type || state.mapType || 'opc-da';
    if (type === 'opc-ua') return uaSources();
    if (type === 'drivers') return driverSources();
    if (type === 'mx') return mxSources();
    return opcDaSources();
}
function sourceMatchesMapType(source, type) {
    type = type || state.mapType || 'opc-da';
    if (!source) return false;
    if (type === 'opc-ua') return isUaSource(source);
    if (type === 'drivers') return isDriverSource(source);
    if (type === 'mx') return isMxSource(source);
    return !isUaSource(source) && !isDriverSource(source) && !isMxSource(source);
}
function mapTypeRoute(type) { return 'tags/maps/' + (type || state.mapType || 'opc-da'); }
function mapTypeLabel(type) {
    type = type || state.mapType || 'opc-da';
    if (type === 'opc-ua') return 'OPC UA';
    if (type === 'drivers') return 'Drivers';
    if (type === 'mx') return 'MX Component';
    return 'OPC DA';
}
function mapTypeConnectivityRoute(type) {
    type = type || state.mapType || 'opc-da';
    if (type === 'opc-ua') return 'connectivity/opc-ua';
    if (type === 'drivers') return 'connectivity/drivers';
    if (type === 'mx') return 'connectivity/mx-component';
    return 'connectivity/opc-da';
}
function syncMapTypeUi() {
    document.querySelectorAll('.map-type-tab').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.mapType === state.mapType);
    });
}
function setMapType(type, opts) {
    type = type || 'opc-da';
    if (type !== 'opc-da' && type !== 'opc-ua' && type !== 'drivers' && type !== 'mx') type = 'opc-da';
    const changed = state.mapType !== type;
    state.mapType = type;
    syncMapTypeUi();
    if (changed || (opts && opts.force)) {
        state.tagPath = '';
        state.uaBrowseTrail = [];
        if (el('tagTree')) el('tagTree').innerHTML = '';
        if (el('tagBreadcrumb')) el('tagBreadcrumb').innerHTML = '';
        if (el('tagStatus')) el('tagStatus').textContent = (type === 'drivers' || type === 'mx')
            ? 'Enter a device address below, or map from known items.'
            : 'Browse all tags, or open folders one level at a time.';
    }
    ensureMapSourceSelection();
    renderMapSourceSelect();
    updateMapSourceHint();
    updateMapEmptyBanner();
    updateMapBrowseUi();
    rerenderMappings();
    if (!(opts && opts.skipNavigate) && document.getElementById('view-tags')?.classList.contains('active')) {
        const route = mapTypeRoute(type);
        if (location.hash !== '#/' + route) history.replaceState(null, '', '#/' + route);
        document.querySelectorAll('.tabbtn').forEach(b => b.classList.toggle('active', b.dataset.route === 'tags/maps' || b.dataset.route === route));
    }
}
function ensureMapSourceSelection() {
    // Maps-tab source selection only applies while the Maps view is active — it must not
    // clobber a source picked on the Connectivity pages (e.g. a UA source selected on the
    // OPC UA tab would otherwise revert to the first DA source immediately).
    if (!document.getElementById('view-tags')?.classList.contains('active')) return;
    const sources = mapTypeSources();
    if (!sources.length) return;
    const current = state.sources.find(s => s.sourceId === state.selectedSourceId);
    if (!current || !sourceMatchesMapType(current)) {
        state.selectedSourceId = sources[0].sourceId;
    }
}
function renderMapSourceSelect() {
    const mapSelect = el('mapSourceSelect');
    if (!mapSelect) return;
    const sources = mapTypeSources();
    mapSelect.innerHTML = sources.map(source =>
        `<option value="${esc(source.sourceId)}">${esc(source.displayName || source.sourceId)}</option>`
    ).join('');
    if (sources.some(s => s.sourceId === state.selectedSourceId)) mapSelect.value = state.selectedSourceId;
    else if (sources.length) mapSelect.value = sources[0].sourceId;
}
function updateMapEmptyBanner() {
    const banner = el('bannerTagsNoSources');
    if (!banner) return;
    const sources = mapTypeSources();
    const none = sources.length === 0;
    banner.style.display = none ? '' : 'none';
    if (none) {
        const label = mapTypeLabel();
        const route = mapTypeConnectivityRoute();
        banner.innerHTML = `No ${esc(label)} sources yet. <button class="btn" type="button" onclick="navigate('${route}')">Add ${esc(label)} Source</button>`;
    } else {
        banner.innerHTML = '';
    }
}
function updateMapBrowseUi() {
    const allBtn = el('btnBrowseAllTags');
    const folderBtn = el('btnBrowseTags');
    const addressBased = state.mapType === 'drivers' || state.mapType === 'mx';
    if (allBtn) allBtn.style.display = addressBased ? 'none' : '';
    if (folderBtn) folderBtn.style.display = addressBased ? 'none' : '';
    if (el('manualItem')) {
        el('manualItem').placeholder = state.mapType === 'opc-ua'
            ? 'NodeId (e.g. ns=2;s=Tag)'
            : state.mapType === 'drivers'
              ? 'Address (e.g. D100, VW100, I0.0)'
              : state.mapType === 'mx'
                ? 'Address (e.g. D100, M10, X20, D100:8)'
                : 'Item ID (e.g. Random.Real8)';
    }
}
function mappingsForMapType(mappings) {
    const ids = new Set(mapTypeSources().map(s => s.sourceId));
    return (mappings || []).filter(m => ids.has(m.sourceId || m.SourceId || 'default'));
}
function setDriverFormType(type) {
    state.driverFormType = type || 'MelsecA3n';
    const s7 = state.driverFormType === 'S7200Ppi';
    if (el('drvA3nStationRow')) el('drvA3nStationRow').style.display = s7 ? 'none' : '';
    if (el('drvS7PpiRow')) el('drvS7PpiRow').style.display = s7 ? '' : 'none';
}
function wzDrvOnTypeChange() {
    const s7 = el('wzDrvType') && el('wzDrvType').value === 'S7200Ppi';
    if (el('wzDrvStationRow')) el('wzDrvStationRow').style.display = s7 ? 'none' : '';
    if (el('wzDrvS7PpiRow')) el('wzDrvS7PpiRow').style.display = s7 ? '' : 'none';
    if (el('wzDrvParity')) el('wzDrvParity').value = s7 ? 'Even' : 'Odd';
}

function sourceTypeLabel(source) {
    if (isUaSource(source)) return 'UA';
    if (isMelsecSource(source)) return 'A3N';
    if (isMxSource(source)) return 'MX';
    if (isS7Source(source)) return 'S7-200';
    return 'DA';
}
function sourceTypeBadge(source) {
    if (isUaSource(source)) return badge('UA', 'partial');
    if (isMelsecSource(source)) return badge('A3N', 'partial');
    if (isMxSource(source)) return badge('MX', 'partial');
    if (isS7Source(source)) return badge('S7-200', 'partial');
    return badge('DA', 'warn');
}
function sourceEndpointSummary(source) {
    if (isUaSource(source)) {
        return esc(source.endpointUrl || source.EndpointUrl || '—');
    }
    if (isMxSource(source)) {
        return esc('MX station ' + (source.logicalStationNumber ?? 0));
    }
    return `${esc(source.host || 'localhost')} · ${esc(source.progId || '')}`;
}
function sourceStatusRowHtml(source) {
    const st = source.connectionState || source.ConnectionState || '';
    const err = source.lastError || source.LastError || '';
    const info = source.serverInfo || source.ServerInfo || '';
    const mode = source.readMode || source.ReadMode || '';
    const wmode = source.writeMode || source.WriteMode || '';
    const errBit = err ? ` · <span class="bad">${esc(err)}</span>` : '';
    const infoBit = info ? ` · ${esc(info)}` : '';
    const modeBit = mode ? ` · <span class="msg" style="font-weight:400">${esc(mode)}</span>` : '';
    const wmodeBit = wmode ? ` · <span class="msg" style="font-weight:400">${esc(wmode)}</span>` : '';
    return `<div class="li source-row"><div><div class="n">${esc(source.displayName || source.sourceId)} ${sourceTypeBadge(source)} ${st ? badge(st, stateClass(st)) : ''}</div><div class="p">${esc(source.sourceId)} · ${sourceEndpointSummary(source)} · ${formatMs(source.updateRateMs)}${infoBit}${modeBit}${wmodeBit}${errBit}</div></div><button class="btn ghost" data-action="select-source-status" data-source-id="${attr(source.sourceId)}">Select</button></div>`;
}
function renderSourcesStatusList() {
    const host = el('sourcesStatusList');
    if (!host) return;
    host.innerHTML = state.sources.length
        ? state.sources.map(sourceStatusRowHtml).join('')
        : '<span class="msg">No sources configured. Click + Add Source.</span>';
}
function daSources() { return state.sources.filter(s => !isUaSource(s)); }
function uaSources() { return state.sources.filter(s => isUaSource(s)); }
function renderSources() {
    const select = el('selectedSource');
    const uaSelect = el('uaSelectedSource');
    const daOpts = opcDaSources().map(source => `<option value="${esc(source.sourceId)}">${esc(source.displayName || source.sourceId)}</option>`).join('');
    const uaOpts = uaSources().map(source => `<option value="${esc(source.sourceId)}">${esc(source.displayName || source.sourceId)}</option>`).join('');
    if (select) select.innerHTML = daOpts;
    if (uaSelect) uaSelect.innerHTML = uaOpts;
    if (!state.editingNewSource && !state.editingNewUaSource && !state.sources.some(source => source.sourceId === state.selectedSourceId) && state.sources.length) {
        state.selectedSourceId = state.sources[0].sourceId;
    }
    ensureMapSourceSelection();
    if (select) select.value = state.selectedSourceId;
    if (uaSelect) uaSelect.value = state.selectedSourceId;
    renderMapSourceSelect();
    el('pSources').textContent = state.sources.length;
    const noSources = state.sources.length === 0;
    const bannerNo = el('bannerNoSources');
    if (bannerNo) bannerNo.style.display = noSources ? '' : 'none';
    if (bannerNo && noSources) bannerNo.innerHTML = 'No sources configured. <button class="btn" type="button" onclick="navigate(\'connectivity/sources\')">Add Source</button>';
    updateMapEmptyBanner();
    updateNoMappingsBanner();
    const sideCount = el('pSourcesSide');
    if (sideCount) {
        const n = daSources().length;
        sideCount.textContent = n + ' source' + (n !== 1 ? 's' : '');
    }
    const uaSide = el('pUaSourcesSide');
    if (uaSide) {
        const n = uaSources().length;
        uaSide.textContent = n + ' source' + (n !== 1 ? 's' : '');
    }
    const list = el('sourcesList');
    if (list) {
        const das = opcDaSources();
        list.innerHTML = das.length ? das.map(source =>
            `<div class="li source-row"><div><div class="n">${esc(source.displayName || source.sourceId)} ${sourceTypeBadge(source)}</div><div class="p">${esc(source.sourceId)} · ${esc(source.host || 'localhost')} · ${esc(source.progId || '')} · ${formatMs(source.updateRateMs)}</div></div><button class="btn ghost" data-action="select-source" data-source-id="${attr(source.sourceId)}">Select</button></div>`
        ).join('') : '<span class="msg">No OPC DA sources configured.</span>';
    }
    const uaList = el('uaSourcesList');
    if (uaList) {
        const uas = uaSources();
        uaList.innerHTML = uas.length ? uas.map(source =>
            `<div class="li source-row"><div><div class="n">${esc(source.displayName || source.sourceId)} ${sourceTypeBadge(source)}</div><div class="p">${esc(source.sourceId)} · ${esc(source.endpointUrl || '')} · ${formatMs(source.updateRateMs)}</div></div><button class="btn ghost" data-action="select-ua-source" data-source-id="${attr(source.sourceId)}">Select</button></div>`
        ).join('') : '<span class="msg">No OPC UA sources configured.</span>';
    }
    renderSourcesStatusList();
    updateMapSourceHint();
    updateMapBrowseUi();
    loadSelectedSourceForm();
    loadSelectedUaSourceForm();
}
function updateMapSourceHint() {
    const hint = el('mapSourceHint');
    if (!hint) return;
    const source = state.sources.find(s => s.sourceId === state.selectedSourceId);
    const melsecLike = source && (isMelsecSource(source) || isMxSource(source));
    hint.textContent = melsecLike ? 'Device address e.g. D100, M10, X20, D100:8' : (source && isS7Source(source) ? 'Siemens address e.g. VW100, I0.0, M10.2, QB0' : '');
    const tgl = el('mapAddressRangesToggle');
    const wrap = el('mapAddressRanges');
    if (tgl) tgl.style.display = melsecLike ? '' : 'none';
    if (!melsecLike && wrap && wrap.style.display !== 'none') {
        // Source switched away from MELSEC — collapse the ranges table.
        wrap.style.display = 'none';
        const btn = el('mapAddressRangesToggle');
        if (btn) btn.textContent = 'Show accepted addresses ▾';
    }
}
// Accepted PLC address ranges (MELSEC devices shared by serial + MX Component drivers).
// Served by GET /api/drivers/mx-component/address-ranges from the same catalog the
// parser enforces, so the table always matches what tag upserts accept.
async function ensureAddressRanges() {
    if (!state.addressRangesCache) {
        const r = await fetch('/api/drivers/mx-component/address-ranges');
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const p = await r.json();
        state.addressRangesCache = p.devices || [];
    }
    return state.addressRangesCache;
}
function addressRangeText(d) {
    if (d.numberBase === 'OctalOrHex') {
        return d.min + '\u2013' + Number(d.max).toString(8).toUpperCase() + '\u2088';
    }
    return String(d.min) + '\u2013' + String(d.max);
}
function addressRangesTableHtml(devices) {
    const rows = devices.map(d => {
        const alias = (d.aliases && d.aliases.length)
            ? ' <span class="msg">(' + d.aliases.map(a => '<span class="mono">' + esc(a) + '</span>').join(', ') + ' alias)</span>'
            : '';
        const suffix = d.bitSuffixAllowed ? ':' + d.maxBitIndex : '';
        return '<tr>'
            + '<td class="mono">' + esc(d.device) + '</td>'
            + '<td>' + esc(d.displayName) + alias + '</td>'
            + '<td>' + esc(d.signalType) + '</td>'
            + '<td>' + esc(d.numberBase === 'OctalOrHex' ? 'Octal/hex' : 'Decimal') + '</td>'
            + '<td class="mono">' + esc(addressRangeText(d)) + suffix + '</td>'
            + '<td class="mono">' + esc(d.example) + '</td>'
            + '</tr>';
    }).join('');
    return '<table class="address-ranges-table"><thead><tr><th>Device</th><th>Meaning</th><th>Type</th><th>Numbering</th><th>Accepted range</th><th>Example</th></tr></thead><tbody>' + rows + '</tbody></table>'
        + '<div class="hint" style="margin-top:6px">Bit-in-word suffix (<span class="mono">D100:8</span>) is only valid on D registers, bits 0\u201315. X/Y use octal digits; hex forms like <span class="mono">Y0F</span> also parse.</div>';
}
async function toggleAddressRanges(containerId, btn) {
    const wrap = el(containerId);
    if (!wrap) return;
    if (wrap.style.display !== 'none') {
        wrap.style.display = 'none';
        if (btn) btn.textContent = 'Show accepted addresses \u25be';
        return;
    }
    try {
        const devices = await ensureAddressRanges();
        wrap.innerHTML = addressRangesTableHtml(devices);
        wrap.style.display = '';
        if (btn) btn.textContent = 'Hide accepted addresses \u25b4';
    } catch (err) {
        wrap.innerHTML = '<span class="msg">\u2717 Could not load accepted addresses: ' + esc(String((err && err.message) || err)) + '</span>';
        wrap.style.display = '';
    }
}
function updateCfgServerInfo(source) {
    const info = el('cfgServerInfo');
    const mode = el('cfgReadMode');
    const wmode = el('cfgWriteMode');
    if (!info) return;
    if (!source || isUaSource(source)) {
        info.textContent = '—';
        if (mode) mode.textContent = '—';
        if (wmode) wmode.textContent = '—';
        return;
    }
    info.textContent = source.serverInfo
        ? source.serverInfo
        : 'Not detected — info appears after the source connects.';
    if (mode) mode.textContent = source.readMode || source.ReadMode || '—';
    if (wmode) wmode.textContent = source.writeMode || source.WriteMode || '—';
}
function updateUaCfgReadMode(source) {
    const mode = el('uaCfgReadMode');
    const wmode = el('uaCfgWriteMode');
    if (!mode && !wmode) return;
    const readMode = (source && (source.readMode || source.ReadMode)) ? (source.readMode || source.ReadMode) : '—';
    const writeMode = (source && (source.writeMode || source.WriteMode)) ? (source.writeMode || source.WriteMode) : '—';
    if (mode) mode.textContent = readMode;
    if (wmode) wmode.textContent = writeMode;
}
function loadSelectedSourceForm() {
    if (state.editingNewSource) return;
    const source = currentSource();
    if (!source || isUaSource(source)) {
        el('cfgSourceId').disabled = false;
        el('cfgSourceId').value = '';
        el('cfgDisplayName').value = '';
        el('cfgProgId').value = '';
        el('cfgHost').value = 'localhost';
        el('cfgUser').value = '';
        el('cfgPass').value = '';
        el('cfgDomain').value = '';
        if (el('cfgIoMode')) el('cfgIoMode').value = 'AutoDetect';
        const ioModeHint = el('ioModeHint'); if (ioModeHint) ioModeHint.textContent = '';
        el('cfgMessage').textContent = source
            ? 'Select a saved OPC DA connection or click New.'
            : 'No OPC DA sources configured. Click + Add Source or New.';
        updateCfgServerInfo(null);
        hideSaveReset();
        return;
    }
    el('cfgSourceId').value = source.sourceId || '';
    el('cfgSourceId').disabled = true;
    el('cfgDisplayName').value = source.displayName || '';
    el('cfgProgId').value = source.progId || '';
    el('cfgHost').value = source.host || 'localhost';
    el('cfgUser').value = source.remoteUsername || '';
    el('cfgPass').value = '';
    el('cfgDomain').value = source.remoteDomain || '';
    if (el('cfgIoMode')) {
        el('cfgIoMode').value = (source.ioMode === 'Sync' || source.ioMode === 'Async20') ? source.ioMode : 'AutoDetect';
        const ioModeHint = el('ioModeHint');
        if (ioModeHint) ioModeHint.textContent = 'Requested: ' + el('cfgIoMode').value + ' · effective: see Read Mode above';
    }
    el('cfgMessage').textContent = (isMelsecSource(source) || isS7Source(source))
        ? 'Serial driver source — edit it on the Drivers page.'
        : (isMxSource(source)
            ? 'MX Component source — edit it on the MX Component page.'
            : 'Editing ' + (source.displayName || source.sourceId) + '.');
    updateCfgServerInfo(source);
    loadGroupsSection();
    hideSaveReset();
}
function loadSelectedUaSourceForm() {
    if (state.editingNewUaSource) return;
    const source = currentSource();
    if (!source || !isUaSource(source)) {
        el('uaCfgSourceId').disabled = false;
        el('uaCfgSourceId').value = '';
        el('uaCfgDisplayName').value = '';
        el('uaCfgEndpointUrl').value = '';
        el('uaCfgSecurityMode').value = 'None';
        el('uaCfgSecurityPolicy').value = 'None';
        el('uaCfgUser').value = '';
        el('uaCfgPass').value = '';
        el('uaCfgUpdateRate').value = String(state.updateRateMs || 1000);
        el('uaCfgMaxMappedTags').value = String(50000);
        el('uaCfgUseSubscriptions').checked = true;
        el('uaCfgMessage').textContent = source
            ? 'Select a saved OPC UA connection or click New.'
            : 'No OPC UA sources configured. Click + Add Source or New.';
        updateUaCfgReadMode(null);
        hideUaSaveReset();
        return;
    }
    el('uaCfgSourceId').value = source.sourceId || '';
    el('uaCfgSourceId').disabled = true;
    el('uaCfgDisplayName').value = source.displayName || '';
    el('uaCfgEndpointUrl').value = source.endpointUrl || '';
    el('uaCfgSecurityMode').value = source.securityMode || 'None';
    el('uaCfgSecurityPolicy').value = source.securityPolicy || 'None';
    el('uaCfgUser').value = source.uaUsername || '';
    el('uaCfgPass').value = '';
    el('uaCfgUpdateRate').value = String(source.updateRateMs || state.updateRateMs || 1000);
    el('uaCfgMaxMappedTags').value = String(source.maxMappedTags || 50000);
    el('uaCfgUseSubscriptions').checked = source.useSubscriptions !== false;
    el('uaCfgMessage').textContent = 'Editing ' + (source.displayName || source.sourceId) + '.';
    updateUaCfgReadMode(source);
    hideUaSaveReset();
}
async function loadSources() {
    const payload = await (await fetch('/api/da/sources', { cache: 'no-store' })).json();
    state.sources = payload.sources || [];
    // Merge live connection status (from /api/dashboard bridge.sources) so the
    // status list and Select-by-type rows show Connected/Faulted + last error.
    const statusBySource = new Map((state.bridgeSources || []).map(s => [String(get(s, 'sourceId') || '').toLowerCase(), s]));
    state.sources.forEach(source => {
        const status = statusBySource.get(String(source.sourceId || '').toLowerCase());
        if (status) {
            source.connectionState = get(status, 'connectionState');
            source.lastError = get(status, 'lastError');
            source.serverInfo = get(status, 'serverInfo');
            source.readMode = get(status, 'readMode') || '';
            source.writeMode = get(status, 'writeMode') || '';
        }
    });
    state.updateRateMs = Number(payload.updateRateMs || state.updateRateMs || 1000);
    state.useSubscriptions = payload.useSubscriptions !== false;
    if (el('cfgUseSubscriptions')) el('cfgUseSubscriptions').checked = state.useSubscriptions;
    if (el('cfgUpdateRate') && document.activeElement !== el('cfgUpdateRate')) el('cfgUpdateRate').value = String(state.updateRateMs);
    renderSources();
    populateLiveValuesSource();
    if (document.getElementById('view-interlinks')?.classList.contains('active')) renderInterlinksView();
}
function updateLiveValuesUi() {
    el('toggleLiveValues').textContent = state.liveValuesEnabled ? 'Disable Live Data' : 'Enable Live Data';
    const filtered = state.liveValuesSource ? ' · ' + state.liveValuesSource : '';
    el('valCount').textContent = state.lastValueCount + ' values' + filtered + (state.liveValuesEnabled ? '' : ' · paused');
}

function populateLiveValuesSource() {
    const select = el('liveValuesSource');
    if (!select) return;
    const current = state.liveValuesSource || '';
    select.innerHTML = '<option value="">All sources</option>' + (state.sources || []).map(source =>
        `<option value="${attr(source.sourceId)}">${esc(source.displayName || source.sourceId)}</option>`
    ).join('');
    select.value = current;
}

function formatMs(value) {
    const n = Number(value ?? 0);
    return n > 0 ? n.toLocaleString(undefined, { maximumFractionDigits: 1 }) + ' ms' : '—';
}

function formatRate(value) {
    const n = Number(value ?? 0);
    return n > 0 ? n.toLocaleString(undefined, { maximumFractionDigits: 1 }) + ' values/s' : '0 values/s';
}

function formatUaDiagnostics(ua) {
    const nodeCount = get(ua, 'mappedNodeCount') ?? 0;
    const lastUpdateUtc = get(ua, 'lastValueUpdateUtc');
    return nodeCount + ' nodes · last node update ' + (lastUpdateUtc ? relTime(lastUpdateUtc) : 'never');
}
function formatPollSaturation(lastPollDurationMs, updateRateMs) {
    const duration = Number(lastPollDurationMs ?? 0);
    const rate = Number(updateRateMs ?? 0);
    if (duration <= 0 || rate <= 0) return { text: 'Waiting for cycle timing…', className: 's' };
    if (duration >= rate) return { text: 'Cycle saturated · read time at or above configured rate.', className: 's bad' };
    if (duration >= rate * 0.8) return { text: 'Cycle budget is getting tight.', className: 's warn' };
    return { text: 'Cycle timing normal.', className: 's' };
}

function formatPollUtilization(lastPollDurationMs, updateRateMs) {
    const duration = Number(lastPollDurationMs ?? 0);
    const rate = Number(updateRateMs ?? 0);
    if (duration <= 0 || rate <= 0) {
        return { width: '0%', className: 'mini-meter-fill', text: 'Cycle budget —' };
    }

    const percent = Math.max(0, Math.round((duration / rate) * 100));
    const clampedPercent = Math.max(0, Math.min(percent, 100));
    const className = percent >= 100
        ? 'mini-meter-fill bad'
        : percent >= 80
            ? 'mini-meter-fill warn'
            : 'mini-meter-fill';

    return {
        width: clampedPercent + '%',
        className,
        text: 'Cycle budget ' + percent + '%'
    };
}
async function loadLogs(force = false) {
    if (state.logsLoaded && !force) return;
    const level = el('logLevel')?.value || 'Information';
    const limit = parseInt(el('logLimit')?.value || '200', 10);
    el('logMessage').textContent = 'Loading logs…';
    const payload = await (await fetch('/api/logs?limit=' + limit + '&level=' + encodeURIComponent(level), { cache: 'no-store' })).json();
    const entries = payload.entries || [];
    el('logEntries').innerHTML = entries.length ? entries.map(entry => {
        const timestamp = locTime(entry.timestampUtc || entry.TimestampUtc);
        const levelText = (entry.level || entry.Level || 'Information');
        const levelClass = levelText.toLowerCase();
        const category = entry.category || entry.Category || 'App';
        const message = entry.message || entry.Message || '';
        const exceptionText = entry.exceptionText || entry.ExceptionText || '';
        return `<div class="log-entry"><div class="meta">${esc(timestamp)} · <span class="lvl ${esc(levelClass)}">${esc(levelText)}</span> · ${esc(category)}</div><div class="message ${esc(levelClass)}">${esc(message)}</div>${exceptionText ? `<div class="exception">${esc(exceptionText)}</div>` : ''}</div>`;
    }).join('') : '<span class="msg">No log entries for this filter yet.</span>';
    el('logMessage').textContent = entries.length + ' entr' + (entries.length !== 1 ? 'ies' : 'y') + ' (level ≥ ' + level + ')';
    state.logsLoaded = true;
}

let diagnosticsActive = false;

// Formats seconds as compact human uptime: 2d 3h · 4h 12m · 5m 20s · 42s.
function fmtUptime(sec) {
    sec = Math.max(0, Math.floor(Number(sec) || 0));
    const d = Math.floor(sec / 86400), h = Math.floor((sec % 86400) / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
    if (d > 0) return d + 'd ' + h + 'h';
    if (h > 0) return h + 'h ' + m + 'm';
    if (m > 0) return m + 'm ' + s + 's';
    return s + 's';
}

// Shared renderer for the MQTT / InfluxDB integration cards on the Diagnostics tab.
// d: { enabled, state, lastError, ...counters }. Colors: connected/running=good,
// error/fault=bad, everything else live=warn, disabled=muted.
function setIntegrationHealth(ids, d, countersText, rateText) {
    if (!ids || !d) return;
    const enabled = d.enabled === true;
    const stateText = enabled ? (d.state || '—') : 'Disabled';
    let cls = 'msg';
    if (enabled) {
        const s = String(d.state || '').toLowerCase();
        cls = s.includes('error') || s.includes('fault') ? 'bad' : (s === 'connected' || s === 'running' ? 'good' : 'warn');
    }
    ids.badge.innerHTML = '<span class="' + cls + '">' + esc(stateText) + '</span>';
    ids.state.textContent = stateText;
    ids.totals.textContent = countersText;
    ids.rate.textContent = rateText;
    const err = d.lastError;
    if (err) { ids.error.style.display = ''; ids.error.innerHTML = '<span class="bad">⚠ Last error:</span> ' + esc(err); }
    else { ids.error.style.display = 'none'; }
}

// Average-to-recent growth percentage over a capped history window.
// Returns null when there is not enough data yet (needs >= 12 samples).
function windowTrendPct(history) {
    if (!history || history.length < 12) return null;
    const q = Math.floor(history.length / 4);
    const earlyAvg = history.slice(0, q).reduce((a, b) => a + b, 0) / q;
    const recentAvg = history.slice(-q).reduce((a, b) => a + b, 0) / q;
    return earlyAvg > 0 ? ((recentAvg - earlyAvg) / earlyAvg) * 100 : 0;
}

// Collapse a long problem list to per-source counts, sorted by count desc:
// [['source-a', 12], ['source-b', 3]]
function groupProblemsBySource(items) {
    const bySource = {};
    (items || []).forEach(it => {
        const sid = get(it, 'sourceId') || '—';
        bySource[sid] = (bySource[sid] || 0) + 1;
    });
    return Object.entries(bySource).sort((a, b) => b[1] - a[1]);
}

async function loadDiagnostics() {
    if (!diagnosticsActive) return;
    try {
        const p = await (await fetch('/api/diagnostics', { cache: 'no-store' })).json();
        renderDiagnostics(p);
    } catch (e) {
        el('diagDaSources').innerHTML = '<span class="bad">✗ ' + esc(e.message) + '</span>';
    }
}

async function refreshPortsInfo() {
    try {
        const r = await (await fetch('/api/status/ports', { cache: 'no-store' })).json();
        const httpPort = r.httpPort ?? r.HttpPort ?? '—';
        const uaPort = r.uaPort ?? r.UaPort ?? '—';
        const httpDefault = r.httpDefault ?? 8080;
        const uaDefault = r.uaDefault ?? 4840;
        const httpAuto = !!r.httpAutoAssigned;
        const uaAuto = !!r.uaAutoAssigned;
        const host = location.hostname || 'localhost';
        const httpEl = el('httpPortVal');
        const uaEl = el('uaPortVal');
        if (httpEl) {
            httpEl.textContent = httpPort;
            httpEl.title = httpAuto ? 'Auto-assigned: the default port ' + httpDefault + ' was in use.' : 'Default port';
        }
        if (uaEl) {
            uaEl.textContent = uaPort;
            uaEl.title = uaAuto ? 'Auto-assigned: the default port ' + uaDefault + ' was in use.' : 'Default port';
        }
        const httpNote = el('httpPortNote');
        if (httpNote) httpNote.textContent = httpAuto
            ? 'auto-assigned from ' + httpDefault + ' · http://' + host + ':' + httpPort
            : 'Dashboard + API · http://' + host + ':' + httpPort;
        const uaNote = el('uaPortNote');
        if (uaNote) uaNote.textContent = uaAuto
            ? 'auto-assigned from ' + uaDefault + ' · ' + (r.uaEndpointClient || '')
            : 'UA server endpoint · ' + (r.uaEndpointClient || '');
        const banner = el('portBanner');
        if (banner) {
            const autoPorts = [];
            if (httpAuto) autoPorts.push('HTTP ' + httpPort + ' (default ' + httpDefault + ' was in use)');
            if (uaAuto) autoPorts.push('OPC UA ' + uaPort + ' (default ' + uaDefault + ' was in use)');
            if (autoPorts.length) {
                banner.style.display = '';
                banner.innerHTML = '&#9888; Bridge is running on auto-assigned port' + (autoPorts.length > 1 ? 's' : '') + ': ' + autoPorts.join('; ') +
                    '. <button class="btn" type="button" onclick="this.parentElement.style.display=\'none\'">Dismiss</button>';
            } else {
                banner.style.display = 'none';
            }
        }
    } catch (e) {
        // ports info is best-effort; never break the dashboard refresh
    }
}

// Fleet strip on ops/monitor: every OpcBridge instance the discovery probe
// found (this instance included, badged Local).
function renderFleet(apps) {
    const listEl = el('fleetList');
    if (!listEl) return;
    const list = (apps && get(apps, 'detectedApps')) || [];
    const countEl = el('fleetCount');
    if (countEl) countEl.textContent = list.length + ' detected';
    listEl.innerHTML = list.length ? list.map(a => {
        const machine = get(a, 'machineName') || '—';
        const version = get(a, 'version') || '—';
        const isLocal = !!get(a, 'isLocal');
        const probeHost = get(a, 'probeHost') || '';
        return `<div class="li"><div style="flex:1"><div class="n">${esc(machine)} ${badge(isLocal ? 'Local' : 'Remote', isLocal ? 'good' : 'msg')}</div><div class="p">${esc(probeHost)} · v${esc(version)}</div></div></div>`;
    }).join('') : '<span class="msg">No other bridge instances detected.</span>';
}

function renderDiagnostics(p) {
    // Bridge Vitals — only metrics NOT shown on ops/monitor (bridge state, DA
    // connection, UA clients/nodes, mapping count, last DA read / UA write and
    // last error live there; repeating them here would double-display data).
    const rt = p.runtime || {};
    el('diagUptime').textContent = fmtUptime(p.uptimeSeconds);
    const vrate = Number(rt.lastPollValueRate || 0);
    el('diagValueRate').textContent = vrate > 0 ? vrate.toFixed(1) : '0';
    el('diagUpdateRate').textContent = rt.updateRateMs ? 'update ' + formatMs(rt.updateRateMs) : '';
    el('diagPollDuration').textContent = formatMs(rt.lastPollDurationMs);
    el('diagSessionId').textContent = rt.sessionId != null && rt.sessionId > 0 ? String(rt.sessionId) : '—';
    el('diagInteractive').textContent = rt.interactiveSession ? 'interactive' : '';
    el('diagHealthUpdated').textContent = 'updated ' + new Date().toLocaleTimeString();

    // Integration health (MQTT / InfluxDB)
    setIntegrationHealth(
        { state: el('diagMqttState'), badge: el('diagMqttBadge'), rate: el('diagMqttRate'), totals: el('diagMqttTotals'), error: el('diagMqttError') },
        p.mqtt,
        (p.mqtt?.publishedCount ?? 0).toLocaleString() + ' published · ' + (p.mqtt?.receivedCount ?? 0).toLocaleString() + ' received',
        '↑ ' + Number(p.mqtt?.publishedRate || 0).toFixed(1) + '/s · ↓ ' + Number(p.mqtt?.receivedRate || 0).toFixed(1) + '/s');
    setIntegrationHealth(
        { state: el('diagInfluxState'), badge: el('diagInfluxBadge'), rate: el('diagInfluxRate'), totals: el('diagInfluxTotal'), error: el('diagInfluxError') },
        p.influx,
        (p.influx?.writtenCount ?? 0).toLocaleString() + ' written',
        Number(p.influx?.writtenRate || 0).toFixed(1) + '/s');

    // Problems — disconnected UA tags being retried
    const problems = p.problems || {};
    const disc = problems.disconnected || [];
    el('diagDiscCount').textContent = disc.length === 1 ? '1 retrying' : disc.length + ' retrying';
    el('diagDisconnected').innerHTML = disc.length === 0
        ? '<span class="good">&#10003; All monitored items connected</span>'
        : disc.length > 8
            ? groupProblemsBySource(disc).map(([sid, count]) =>
                `<div class="li"><div style="flex:1"><div class="n">${esc(sid)}</div><div class="p">${count} tag${count === 1 ? '' : 's'} auto-retrying</div></div><span class="warn">grouped</span></div>`).join('')
            : disc.map(d =>
                `<div class="li"><div style="flex:1"><div class="n">${esc(get(d, 'sourceId') || '')}</div><div class="p">${esc(get(d, 'itemId') || '')}</div></div><span class="warn">auto-retry</span></div>`).join('');

    // Problems — bad-quality tags
    const badTotal = problems.badQualityTotal || 0;
    const badItems = problems.badQuality || [];
    el('diagBadCount').textContent = badTotal === 1 ? '1 affected' : badTotal + ' affected';
    el('diagBadQuality').innerHTML = badTotal === 0
        ? '<span class="good">&#10003; No bad-quality tags</span>'
        : badItems.length > 8
            ? groupProblemsBySource(badItems).map(([sid, count]) =>
                `<div class="li"><div style="flex:1"><div class="n">${esc(sid)}</div><div class="p">${count} tag${count === 1 ? '' : 's'} bad quality</div></div><span class="bad">grouped</span></div>`).join('')
                + (badTotal > badItems.length ? `<div class="li"><span class="msg">+ ${badTotal - badItems.length} more…</span></div>` : '')
            : badItems.map(b => `<div class="li"><div style="flex:1"><div class="n">${esc(get(b, 'sourceId') || '')}</div><div class="p">${esc(get(b, 'itemId') || '')}</div></div><span class="bad">bad</span></div>`).join('')
                + (badTotal > badItems.length ? `<div class="li"><span class="msg">+ ${badTotal - badItems.length} more…</span></div>` : '');

    // Source Diagnostics — reuse state data from /api/dashboard (all source types)
    const sources = state.sources || [];
    const rateGroups = (state.rateGroups || []);
    const daHtml = sources.length ? sources.map(src => {
        const sid = get(src, 'sourceId') || 'default';
        const conn = get(src, 'connectionState') || '—';
        const latency = formatMs(get(src, 'lastDaReadDurationMs'));
        const srcGroups = rateGroups.filter(g => g.sourceId === sid);
        const totalTags = srcGroups.reduce((sum, g) => sum + (g.tagCount || 0), 0);
        const endpoint = get(src, 'endpointSummary') || '';
        const groupRows = srcGroups.length ? srcGroups.map(g => {
            const budget = Math.round(g.cycleBudgetPct || 0);
            const budgetCls = budget >= 80 ? 'bad' : (budget >= 50 ? 'warn' : 'good');
            return `<div class="li"><div style="flex:1"><div class="n">${formatMs(g.rateMs)} · ${g.tagCount} tags</div><div class="p">budget <span class="${budgetCls}">${budget}%</span> · limit ${g.tagLimit || '—'}</div></div></div>`;
        }).join('') : '<span class="msg">No rate groups.</span>';
        const lastRead = get(src,'lastDaReadUtc') ? 'last read ' + relTime(get(src,'lastDaReadUtc')) : 'no reads yet';
        const srcErr = get(src,'lastError');
        return `<div class="li"><div style="flex:1"><div class="n">${esc(get(src,'displayName') || sid)} ${sourceTypeBadge(src)} ${badge(conn, stateClass(conn))}</div><div class="p">${endpoint ? esc(endpoint) + ' · ' : ''}Latency: ${latency} · ${totalTags} tags in ${srcGroups.length} rate group(s)</div><div class="p">${lastRead}${srcErr ? ' · <span class="bad" title="Last source error">' + esc(srcErr) + '</span>' : ''}</div></div></div>${groupRows}`;
    }).join('') : '<span class="msg">No sources configured.</span>';
    el('diagDaSources').innerHTML = daHtml;
    el('diagDaSummary').textContent = sources.length + ' source' + (sources.length !== 1 ? 's' : '');

    // Time Sync — DA server clock vs bridge clock
    const timeSyncHtml = sources.length ? sources.map(src => {
        const sid = get(src, 'sourceId') || 'default';
        const name = get(src, 'displayName') || sid;
        const offset = get(src, 'daClockOffsetMs');
        let offsetText, offsetCls;
        if (offset === null || offset === undefined) {
            offsetText = '—'; offsetCls = 'msg';
        } else {
            const ms = Number(offset);
            offsetText = (ms >= 0 ? '+' : '') + ms.toFixed(1) + ' ms';
            offsetCls = Math.abs(ms) > 500 ? 'bad' : (Math.abs(ms) > 100 ? 'warn' : 'good');
        }
        const bridgeTime = get(src, 'lastDaReadUtc') ? shortTime(get(src, 'lastDaReadUtc')) : '—';
        return `<div class="li"><div style="flex:1"><div class="n">${esc(name)}</div><div class="p">DA server clock offset: <span class="${offsetCls}">${offsetText}</span> · bridge read at ${bridgeTime}</div></div></div>`;
    }).join('') : '<span class="msg">No sources.</span>';
    el('diagTimeSync').innerHTML = timeSyncHtml;


    // UA Sessions
    const sessions = (p.ua && p.ua.sessions) || [];
    el('diagUaSessionCount').textContent = sessions.length + ' active';
    el('diagUaSessions').innerHTML = sessions.length ? sessions.map(s => {
        const last = relTime(s.lastContactUtc);
        return `<div class="li"><div style="flex:1"><div class="n">${esc(s.clientName || 'anonymous')}</div><div class="p">${s.endpointUrl ? esc(s.endpointUrl) + ' · ' : ''}session #${s.sessionId ?? '—'}</div><div class="p">${s.subscriptions} subs · ${s.monitoredItems} monitored · ${s.publishRequestsInQueue} publish queued · ${s.totalPublishCount} total publishes</div><div class="p">last contact ${last}</div></div></div>`;
    }).join('') : '<span class="msg">No active UA sessions.</span>';

    // UA Subscriptions
    const subs = (p.ua && p.ua.subscriptions) || [];
    el('diagUaSubCount').textContent = subs.length + ' active';
    el('diagUaSubscriptions').innerHTML = subs.length ? subs.map(s => {
        return `<div class="li"><div style="flex:1"><div class="n">${esc(s.clientName || 'anonymous')} · sub #${s.subscriptionId}</div><div class="p">${s.monitoredItems} monitored · ${formatMs(s.publishingIntervalMs)} interval · ${s.dataChangeNotifications} data changes · ${s.totalNotifications} total notifs</div><div class="p">${s.publishRequests} publish reqs · ${s.latePublishRequests} late</div></div></div>`;
    }).join('') : '<span class="msg">No active subscriptions.</span>';

    // UA Bandwidth
    const bw = (p.bridge && p.bridge.uaBandwidth) || {};
    const nps = Number(bw.notificationsPerSec || 0);
    el('diagNotifPerSec').textContent = nps.toFixed(1);
    const bps = Number(bw.estimatedBytesPerSec || 0);
    el('diagBandwidth').textContent = bps < 1024 ? bps.toFixed(0) + ' B/s' : (bps / 1024).toFixed(1) + ' KB/s';
    el('diagTotalNotif').textContent = (bw.totalNotifications || 0).toLocaleString() + ' total';

    // Write Queue
    const wq = (p.bridge && p.bridge.writeQueue) || {};
    el('diagWqDepth').textContent = String(wq.currentDepth ?? '—');
    const enq = Number(wq.totalEnqueued || 0);
    const ok = Number(wq.totalSucceeded || 0);
    const fail = Number(wq.totalFailed || 0);
    const rate = enq > 0 ? ((ok / enq) * 100).toFixed(1) + '%' : '—';
    const rateCls = enq > 0 ? (fail > 0 ? 'warn' : 'good') : 'msg';
    el('diagWqRate').innerHTML = '<span class="' + rateCls + '">' + rate + '</span>';
    el('diagWqTotals').textContent = enq + ' enqueued · ' + ok + ' ok · ' + fail + ' failed';

    // STA Thread Health
    const sta = (p.bridge && p.bridge.staThreads) || [];
    el('diagStaThreads').innerHTML = sta.length ? sta.map(t => {
        const aliveCls = t.alive ? 'good' : 'bad';
        const aliveBadge = t.alive ? badge('Alive', 'good') : badge('Dead', 'bad');
        const last = t.lastActionUtc ? relTime(t.lastActionUtc) : 'never';
        const qCls = t.queuedItems >= 50 ? 'bad' : (t.queuedItems >= 10 ? 'warn' : 'good');
        return `<div class="li"><div style="flex:1"><div class="n">${esc(t.sourceId)} ${aliveBadge}</div><div class="p">queued: <span class="${qCls}">${t.queuedItems}</span> · last action ${last}</div></div></div>`;
    }).join('') : '<span class="msg">No STA threads (non-Windows or no sources connected).</span>';
}


async function loadAppInfo(force = false) {
    if (state.appInfoLoaded && !force) return;
    const payload = await (await fetch('/api/app-info', { cache: 'no-store' })).json();
    el('aboutName').textContent = payload.name || 'OpcBridge.App';
    el('aboutVersion').textContent = payload.version || '—';
    el('aboutInfoVersion').textContent = payload.informationalVersion || '—';
    el('aboutFramework').textContent = payload.framework || '—';
    el('aboutArchitecture').textContent = payload.processArchitecture || '—';
    el('aboutOs').textContent = payload.osDescription || '—';
    el('aboutMachine').textContent = payload.machineName || '—';
    el('aboutCreator').textContent = payload.creator || '—';
    el('aboutSection').textContent = payload.section || '—';
    state.appInfoLoaded = true;
}

let helpLoaded = false;
const inlineFmt = (s) => s.replace(/\*\*(.+?)\*\*/g, '<b>$1</b>').replace(/`([^`]+)`/g, '<code>$1</code>');
function renderMarkdown(md) {
    const lines = md.replace(/\r\n/g, '\n').split('\n');
    let html = '', listType = null, inTable = false, inCode = false, tableHeader = false;
    const closeList = () => { if (listType) { html += listType === 'ol' ? '</ol>' : '</ul>'; listType = null; } };
    const openList = (t) => { if (listType !== t) { closeList(); html += t === 'ol' ? '<ol>' : '<ul>'; listType = t; } };
    const closeTable = () => { if (inTable) { html += '</tbody></table>'; inTable = false; } };
    for (let i = 0; i < lines.length; i++) {
        let line = lines[i];
        if (/^```/.test(line)) {
            if (inCode) { html += '</code></pre>'; inCode = false; }
            else { closeList(); closeTable(); html += '<pre><code>'; inCode = true; }
            continue;
        }
        if (inCode) { html += line + '\n'; continue; }
        if (/^---\s*$/.test(line)) { closeList(); closeTable(); html += '<hr>'; continue; }
        if (/^#\s+/.test(line)) { closeList(); closeTable(); html += `<h1>${inlineFmt(line.replace(/^#\s+/, ''))}</h1>`; continue; }
        if (/^##\s+/.test(line)) { closeList(); closeTable(); html += `<h2>${inlineFmt(line.replace(/^##\s+/, ''))}</h2>`; continue; }
        if (/^###\s+/.test(line)) { closeList(); closeTable(); html += `<h3>${inlineFmt(line.replace(/^###\s+/, ''))}</h3>`; continue; }
        if (/^####\s+/.test(line)) { closeList(); closeTable(); html += `<h4>${inlineFmt(line.replace(/^####\s+/, ''))}</h4>`; continue; }
        const olItem = line.match(/^\d+\.\s+(.*)$/);
        if (olItem) { closeTable(); openList('ol'); html += `<li>${inlineFmt(olItem[1])}</li>`; continue; }
        if (/^\*\s+|^-\s+/.test(line)) { closeTable(); openList('ul'); html += `<li>${inlineFmt(line.replace(/^\*\s+|^-\s+/, ''))}</li>`; continue; }
        closeList();
        if (/^\|/.test(line)) {
            if (line.replace(/\s/g, '').match(/^\|[-:|]+\|$/)) { tableHeader = true; continue; }
            const cells = line.split('|').filter((_, j, a) => j > 0 && j < a.length - 1).map(c => c.trim());
            if (!inTable) { html += '<table><thead><tr>'; html += cells.map(c => `<th>${inlineFmt(c)}</th>`).join(''); html += '</tr></thead><tbody>'; inTable = true; tableHeader = false; }
            else if (tableHeader) { tableHeader = false; continue; }
            else { html += '<tr>' + cells.map(c => `<td>${inlineFmt(c)}</td>`).join('') + '</tr>'; }
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
async function loadHelp() {
    if (helpLoaded) return;
    const p = await (await fetch('/api/help', { cache: 'no-store' })).json();
    const groups = (p.markdown || '').split(/\r?\n===\r?\n/).filter(s => s.trim());
    
    const renderGroup = (groupMarkdown, containerId, openCount = 1) => {
        const sections = groupMarkdown.split(/\r?\n---\r?\n/).filter(s => s.trim());
        const container = el(containerId);
        if (!container) return;
        container.innerHTML = sections.map((section, i) => {
            const titleMatch = section.match(/^#\s+(.+)/m);
            const title = titleMatch ? titleMatch[1] : 'Section';
            const body = renderMarkdown(section.replace(/^#\s+.+/m, ''));
            const openAttr = i < openCount ? ' open' : '';
            return `<details class="help-section"${openAttr}><summary>${esc(title)}</summary><div class="help-body">${body}</div></details>`;
        }).join('');
    };
    
    renderGroup(groups[0] || '', 'helpContent1');
    renderGroup(groups[1] || '', 'helpContent2');
    renderGroup(groups[2] || '', 'helpContent3');
    
    helpLoaded = true;
}

function switchHelpSubTab(tabName) {
    document.querySelectorAll('.help-subtab').forEach(btn => {
        btn.classList.toggle('active', btn.textContent.toLowerCase().replace(/\s+/g, '-') === tabName);
    });
    document.querySelectorAll('.help-subtab-content').forEach(content => {
        content.classList.toggle('active', content.id === 'help-' + tabName);
    });
}

async function resolveSessionBanner() {
    const banner = el('sessionBanner');
    if (!banner) return;
    banner.innerHTML = '⚠ Relaunching bridge into the interactive desktop session… this page will reconnect automatically.';
    state.sessionBannerDismissed = true;
    try {
        const r = await fetch('/api/session/resolve', { method: 'POST' });
        const j = await r.json().catch(() => ({}));
        if (!r.ok || j.status !== "ok") {
            state.sessionBannerDismissed = false;
            banner.style.display = '';
            banner.innerHTML = '⚠ Resolve failed: ' + esc(j.message || j.status || 'unknown error') + ' <button class="btn" type="button" onclick="resolveSessionBanner()">Retry</button> <button class="btn" type="button" onclick="state.sessionBannerDismissed=true;el(\'sessionBanner\').style.display=\'none\'">Dismiss</button>';
            return;
        }
        banner.style.display = 'none';
    } catch (e) {
        state.sessionBannerDismissed = false;
        banner.style.display = '';
        banner.innerHTML = '⚠ Resolve failed: ' + esc(e.message) + ' <button class="btn" type="button" onclick="resolveSessionBanner()">Retry</button> <button class="btn" type="button" onclick="state.sessionBannerDismissed=true;el(\'sessionBanner\').style.display=\'none\'">Dismiss</button>';
    }
}
async function refresh() {
    try {
        const lvSource = state.liveValuesSource || '';
        const p = await (await fetch('/api/dashboard?limit=2000' + (lvSource ? '&sourceId=' + encodeURIComponent(lvSource) : ''), { cache: 'no-store' })).json();
        const b = p.bridge || p.Bridge || {};
        const ua = p.ua || p.Ua || {};
        const vs = p.values || p.Values || [];
         const sources = get(b, 'sources') || [];
         state.bridgeSources = sources;
         const apps = p.apps || p.Apps || {};
         el('dot').className = 'dot';
         el('clock').textContent = new Date().toLocaleTimeString();
         el('pBridge').innerHTML = badge(get(b, 'bridgeState') || '—', stateClass(get(b, 'bridgeState')));
         el('pDa').innerHTML = badge(get(b, 'daConnectionState') || '—', stateClass(get(b, 'daConnectionState')));
         el('pUa').innerHTML = badge(get(ua, 'state') || '—', stateClass(get(ua, 'state')));
         el('pTags').textContent = get(b, 'mappingCount') ?? 0;
         el('pApps').textContent = get(apps, 'detectedCount') ?? 1;
         renderFleet(apps);
        el('bridgeState').innerHTML = badge(get(b, 'bridgeState') || '—', stateClass(get(b, 'bridgeState')));
        const sessionBanner = el('sessionBanner');
        if (sessionBanner) {
            if (get(b, 'sessionId') === 0 && get(b, 'interactiveSession') === false) {
                if (state.sessionBannerDismissed) {
                    sessionBanner.style.display = 'none';
                } else {
                    sessionBanner.style.display = '';
                    sessionBanner.innerHTML = '⚠ This bridge runs in a non-interactive Windows session (session 0). Session-bound OPC DA servers (GX Simulator via MX OPC, or any simulator using session-scoped shared memory) will not deliver values. <button class="btn" type="button" onclick="resolveSessionBanner()">Resolve</button> <button class="btn" type="button" onclick="state.sessionBannerDismissed=true;el(\'sessionBanner\').style.display=\'none\'">Dismiss</button>';
                }
            } else {
                sessionBanner.style.display = 'none';
                state.sessionBannerDismissed = false;
            }
        }
        const err = get(b, 'lastError');
        el('lastError').textContent = err || 'No errors';
        el('lastError').className = 's' + (err ? ' bad' : '');
        el('daState').innerHTML = badge(get(b, 'daConnectionState') || '—', stateClass(get(b, 'daConnectionState')));
        el('lastDaRead').textContent = relTime(get(b, 'lastDaReadUtc'));
        el('lastDaReadCount').textContent = (get(b, 'lastDaReadCount') ?? 0) + ' values';
        el('lastUaWrite').textContent = relTime(get(b, 'lastUaWriteUtc'));
        el('lastUaWriteCount').textContent = (get(b, 'lastUaWriteCount') ?? 0) + ' values · ' + formatMs(get(b, 'lastPollDurationMs')) + ' · ' + formatRate(get(b, 'lastPollValueRate'));
        el('uaState').innerHTML = badge(get(ua, 'state') || '—', stateClass(get(ua, 'state')));
        el('uaClients').textContent = (get(ua, 'connectedClientCount') ?? 0) + ' clients';
        const updateRateMs = Number(get(b, 'updateRateMs') || state.updateRateMs || 1000);
        const pollSaturation = formatPollSaturation(get(b, 'lastPollDurationMs'), updateRateMs);
        const pollUtilization = formatPollUtilization(get(b, 'lastPollDurationMs'), updateRateMs);
        state.updateRateMs = updateRateMs;
        state.valuesByKey = new Map(vs.map(v => [valueKey(get(v, 'sourceId') || 'default', get(v, 'itemId') || get(v, 'daItemId')), v]));
        state.disconnectedKeys = new Set((p.disconnected || []).map(d => valueKey(get(d, 'sourceId') || '', get(d, 'itemId') || '')));
        state.badQualityKeys = new Set((p.badQuality || []).map(d => valueKey(get(d, 'sourceId') || '', get(d, 'itemId') || '')));
        state.disconnectedSources = new Set((sources || []).filter(s => String(get(s, 'connectionState') || '').toLowerCase() !== 'connected').map(s => String(get(s, 'sourceId') || '')));
        updateFaceplateLiveValues();
        if (state.diagramLoaded && document.querySelector('.tabbtn.active')?.dataset.tab === 'diagram') {
            renderDiagram();
        }
        // Maps rows carry connection badges driven by these sets — re-render while the
        // Maps tab is visible so Disc/Bad state tracks the live payload.
        if (document.querySelector('.tabbtn.active')?.dataset.tab === 'tags') {
            rerenderMappings();
        }
        el('updateRate').textContent = updateRateMs + ' ms';
        el('pollUtilizationFill').style.width = pollUtilization.width;
        el('pollUtilizationFill').className = pollUtilization.className;
        el('uaEndpoint').textContent = get(ua, 'endpointUrl') || '—';
        el('uaConnectUrl').textContent = get(ua, 'connectUrl') || get(ua, 'endpointUrl') || '—';
        el('uaDiagnostics').textContent = formatUaDiagnostics(ua);
        el('pollSaturation').className = pollSaturation.className;
        if (document.activeElement !== el('cfgUpdateRate')) el('cfgUpdateRate').value = String(updateRateMs);
        el('mappingCount').textContent = (get(b, 'mappingCount') ?? 0) + ' tags';
        refreshPortsInfo();
        el('uaEndpoint').textContent = get(ua, 'endpointUrl') || '—';
        el('uaDiagnostics').textContent = formatUaDiagnostics(ua);
        const srcCountH = el('sourceCountH'); if (srcCountH) srcCountH.textContent = sources.length + ' source' + (sources.length !== 1 ? 's' : '');
        const tagSrcStatus = el('tagSourceStatus');
        if (tagSrcStatus) {
            const selSrc = sources.find(s => (s.sourceId || s.SourceId) === state.selectedSourceId);
            if (selSrc) {
                const cs = get(selSrc, 'connectionState') || '—';
                tagSrcStatus.innerHTML = badge(cs, stateClass(cs));
            } else if (state.editingNewSource) {
                tagSrcStatus.innerHTML = '<span class="msg">unsaved source</span>';
            } else {
                tagSrcStatus.innerHTML = '<span class="msg">—</span>';
            }
        }
        // Refresh live connection status on the Connectivity status list too.
        if (document.getElementById('view-connection')?.classList.contains('active')) {
            const statusBySource = new Map(sources.map(s => [String(get(s, 'sourceId') || '').toLowerCase(), s]));
            state.sources.forEach(source => {
                const status = statusBySource.get(String(source.sourceId || '').toLowerCase());
                if (status) {
                    source.connectionState = get(status, 'connectionState');
                    source.lastError = get(status, 'lastError');
                    source.serverInfo = get(status, 'serverInfo');
                    source.readMode = get(status, 'readMode') || '';
                    source.writeMode = get(status, 'writeMode') || '';
                }
            });
            renderSourcesStatusList();
        }
        // Keep the config forms' Detected Server / Read Mode lines live.
        const activeView = document.querySelector('.view.active')?.id || '';
        if (activeView === 'view-opc-da') {
            const selId = String(state.selectedSourceId || '').toLowerCase();
            const current = state.sources.find(s => String(s.sourceId || '').toLowerCase() === selId);
            const status = sources.find(s => String(get(s, 'sourceId') || '').toLowerCase() === selId);
            if (current && status) {
                current.connectionState = get(status, 'connectionState');
                current.lastError = get(status, 'lastError');
                current.serverInfo = get(status, 'serverInfo');
                current.readMode = get(status, 'readMode') || '';
                current.writeMode = get(status, 'writeMode') || '';
            }
            updateCfgServerInfo(current);
        } else if (activeView === 'view-opc-ua') {
            const selId = String(state.selectedSourceId || '').toLowerCase();
            const current = state.sources.find(s => String(s.sourceId || '').toLowerCase() === selId);
            const status = sources.find(s => String(get(s, 'sourceId') || '').toLowerCase() === selId);
            if (current && status) {
                current.readMode = get(status, 'readMode') || '';
                current.writeMode = get(status, 'writeMode') || '';
            }
            updateUaCfgReadMode(current);
        }
        el('sourceStatusList').innerHTML = sources.length ? sources.map(source => {
            const connState = get(source,'connectionState') || '—';
            const connClass = stateClass(connState);
            const readMode = get(source,'readMode') || '';
            const writeMode = get(source,'writeMode') || '';
            const ioBit = (readMode || writeMode) ? ' · <span style="font-weight:400">' + esc([readMode, writeMode].filter(Boolean).join(' · ')) + '</span>' : '';
            return `<div class="li"><div style="flex:1"><div class="n">${esc(get(source,'displayName') || get(source,'sourceId'))} ${badge(connState, connClass)}</div><div class="p">${esc(get(source,'sourceId'))} · ${esc(get(source,'host') || '')} · ${esc(get(source,'progId') || '')}${ioBit}</div><div class="p">${formatMs(get(source,'updateRateMs'))} · ${(get(source,'lastDaReadCount') ?? 0)} values in ${formatMs(get(source,'lastDaReadDurationMs'))}${get(source,'lastError') ? ' · <span class="bad">' + esc(get(source,'lastError')) + '</span>' : ''}</div></div></div>`;
        }).join('') : '<span class="msg">No source status yet.</span>';
        const rateGroups = get(b, 'rateGroups') || [];
        const alarmBar = el('rateAlarmBar');
        if (alarmBar) {
            const problems = rateGroups.filter(g => g.status === 'limit-exceeded' || g.status === 'saturated');
            const warnings = rateGroups.filter(g => g.status === 'warning');
            if (problems.length > 0) {
                alarmBar.style.display = 'flex';
                alarmBar.className = 'alarm-bar bad';
                alarmBar.innerHTML = problems.map(g => `${esc(g.sourceId)} ${formatMs(g.rateMs)}: ${g.status === 'limit-exceeded' ? g.tagCount + '/' + g.tagLimit + ' tags exceed limit' : Math.round(g.cycleBudgetPct) + '% cycle budget'}`).join(' · ');
            } else if (warnings.length > 0) {
                alarmBar.style.display = 'flex';
                alarmBar.className = 'alarm-bar warning';
                alarmBar.innerHTML = warnings.map(g => `${esc(g.sourceId)} ${formatMs(g.rateMs)}: ${Math.round(g.cycleBudgetPct)}% cycle budget`).join(' · ');
            } else if (rateGroups.length > 0) {
                alarmBar.style.display = 'flex';
                alarmBar.className = 'alarm-bar ok';
                alarmBar.textContent = rateGroups.length + ' rate group' + (rateGroups.length !== 1 ? 's' : '') + ' · all within limits';
            } else {
                alarmBar.style.display = 'none';
            }
        }
        const res = get(b, 'resources');
        const resH = el('resHandles'); const resGU = el('resGdiUser');
        const resA = el('resAssessment'); const resAD = el('resAssessmentDetail');
        if (resH && resGU) {
            if (res && res.supported) {
                resH.textContent = String(res.handleCount ?? '—');
                resGU.textContent = (res.gdiObjects ?? '—') + ' / ' + (res.userObjects ?? '—');

                // Track handle history for leak detection (keep last 60 samples ≈ 5 min at 5s intervals)
                const hc = Number(res.handleCount ?? 0);
                if (hc > 0) {
                    if (state.handleBaseline === null) state.handleBaseline = hc;
                    state.handleHistory.push(hc);
                    if (state.handleHistory.length > 60) state.handleHistory.shift();
                }
                const gdiN = Number(res.gdiObjects ?? 0);
                const userN = Number(res.userObjects ?? 0);
                if (gdiN > 0) {
                    state.gdiHistory.push(gdiN);
                    if (state.gdiHistory.length > 60) state.gdiHistory.shift();
                }
                if (userN > 0) {
                    state.userHistory.push(userN);
                    if (state.userHistory.length > 60) state.userHistory.shift();
                }

                if (resA && resAD) {
                    const gdi = gdiN;
                    const user = userN;
                    const baseline = state.handleBaseline ?? hc;
                    const drift = hc - baseline;
                    const gdiPct = (gdi / 10000) * 100;
                    const userPct = (user / 10000) * 100;

                    // Growth trends from history (handles + GDI + USER)
                    const trendPct = windowTrendPct(state.handleHistory) ?? 0;
                    const trend = trendPct > 15 ? 'rising' : (trendPct < -5 ? 'falling' : 'stable');
                    const gdiTrend = windowTrendPct(state.gdiHistory) ?? 0;
                    const userTrend = windowTrendPct(state.userHistory) ?? 0;

                    let verdict, cls, detail;
                    if (gdiPct >= 80 || userPct >= 80) {
                        verdict = 'Critical'; cls = 'bad';
                        detail = 'GDI/USER near 10,000 limit — restart the app to avoid crash.';
                    } else if (gdiPct >= 50 || userPct >= 50) {
                        verdict = 'Warning'; cls = 'warn';
                        detail = 'GDI/USER above 50% of the 10,000 per-process limit.';
                    } else if ((drift > 200 && trend === 'rising') || gdiTrend > 15 || userTrend > 15) {
                        verdict = 'Watch'; cls = 'warn';
                        const parts = [];
                        if (drift > 200 && trend === 'rising') parts.push('handles +' + Math.round(trendPct) + '% trend, +' + drift + ' since start');
                        if (gdiTrend > 15) parts.push('GDI +' + Math.round(gdiTrend) + '% trend');
                        if (userTrend > 15) parts.push('USER +' + Math.round(userTrend) + '% trend');
                        detail = parts.join('; ') + ' — possible leak.';
                    } else if (drift > 500) {
                        verdict = 'Watch'; cls = 'warn';
                        detail = 'Handle count +' + drift + ' above baseline. Monitor for continued growth.';
                    } else {
                        verdict = 'Normal'; cls = 'good';
                        detail = 'Handles stable (baseline ' + baseline + ', drift ' + (drift >= 0 ? '+' : '') + drift + '). GDI/USER within safe range.';
                    }

                    resA.innerHTML = '<span class="' + cls + '">' + verdict + '</span>';
                    resAD.textContent = detail;
                }
            } else {
                resH.textContent = '—'; resGU.textContent = 'n/a (non-Windows)';
                if (resA) resA.innerHTML = '<span class="msg">n/a</span>';
                if (resAD) resAD.textContent = 'Resource counters are Windows-only.';
            }
        }
        state.lastValueCount = get(p, 'valuesTotal') ?? vs.length;
        updateLiveValuesUi();
        if (state.liveValuesEnabled) {
            el('values').innerHTML = vs.length ? vs.map(it => {
                const g = get(it, 'isGood');
                const q = get(it, 'daQuality');
                const sourceId = get(it, 'sourceId');
                const itemId = get(it, 'itemId') || get(it, 'daItemId');
                const value = String(get(it, 'value') ?? '');
                const timestamp = locTime(get(it, 'timestampUtc'));
                const timestampShort = shortTime(get(it, 'timestampUtc'));
                return `<tr><td><code title="${attr(sourceId)}">${esc(sourceId)}</code></td><td><code title="${attr(itemId)}">${esc(itemId)}</code></td><td class="mono" title="${attr(value)}">${esc(value)}</td><td class="msg" title="${attr(get(it, 'dataType') || '')}">${esc(get(it, 'dataType') || '—')}</td><td class="msg" title="Update rate">${formatMs(get(it, 'updateRate'))}</td><td title="${attr(String(q ?? ''))}"><span class="quality">${badge(g ? 'Good' : 'Bad', g ? 'good' : 'bad')} <span class="${g ? 'good' : 'bad'}">(${q})</span></span></td><td class="msg timestamp" title="${attr(timestamp)}">${esc(timestampShort)}</td></tr>`;
            }).join('') : '<tr><td colspan="7" class="msg">No values yet.</td></tr>';
        }
    } catch (e) {
        el('dot').className = 'dot off';
        el('clock').textContent = 'offline';
        if (state.liveValuesEnabled) {
            el('values').innerHTML = `<tr><td colspan="7" class="bad">${esc(e.message)}</td></tr>`;
        }
    }
}
async function loadInterlinks() {
    const p = await (await fetch('/api/interlinks', { cache: 'no-store' })).json();
    state.interlinks = p.links || [];
    if (document.getElementById('view-interlinks')?.classList.contains('active')) renderInterlinksView();
}

async function loadMappings() {
    const p = await (await fetch('/api/mappings', { cache: 'no-store' })).json();
    const mappings = p.mappings || [];
    state.mappings = mappings;
    const view = applyMappingView(mappings);
    const typed = mappingsForMapType(mappings);
    el('mappedList').innerHTML = renderMappingRows(view);
    if (el('mapCount')) el('mapCount').textContent = view.length + (view.length !== typed.length ? ' / ' + typed.length + ' mappings' : ' mappings');
    updateNoMappingsBanner();
    refreshTagBrowserMappedBadges();
    if (document.getElementById('view-interlinks')?.classList.contains('active')) renderInterlinksView();
}
function refreshTagBrowserMappedBadges() {
    const tree = el('tagTree');
    if (!tree) return;
    const mappedKeys = new Set((state.mappings || []).map(m => valueKey(m.sourceId || m.SourceId || 'default', m.itemId || m.ItemId || m.daItemId || m.DaItemId)));
    tree.querySelectorAll('button[data-action="add-tag"]').forEach(button => {
        const sourceId = button.dataset.sourceId || '';
        const itemId = button.dataset.itemId || '';
        const actions = button.closest('.li-actions');
        if (!actions) return;
        const isMapped = mappedKeys.has(valueKey(sourceId, itemId));
        let badge = actions.querySelector('.mapped-badge');
        if (isMapped && !badge) {
            badge = document.createElement('span');
            badge.className = 'mapped-badge';
            badge.textContent = 'Mapped';
            actions.insertBefore(badge, button);
        } else if (!isMapped && badge) {
            badge.remove();
        }
    });
}
async function loadMqttConfig() {
    try {
        const cfg = await (await fetch('/api/mqtt/config', { cache: 'no-store' })).json();
        if (el('mqttEnabled')) el('mqttEnabled').checked = !!cfg.enabled;
        if (el('mqttBrokerUrl')) el('mqttBrokerUrl').value = cfg.brokerUrl || '';
        if (el('mqttClientId')) el('mqttClientId').value = cfg.clientId || '';
        if (el('mqttUser')) el('mqttUser').value = cfg.userName || '';
        if (el('mqttPass')) el('mqttPass').value = cfg.password || '';
        if (el('mqttTls')) el('mqttTls').checked = !!cfg.tls;
        if (el('mqttIgnoreCert')) el('mqttIgnoreCert').checked = !!cfg.ignoreCertErrors;
        if (el('mqttPrefix')) el('mqttPrefix').value = cfg.topicPrefix || 'bridge/tags';
        if (el('mqttFields')) el('mqttFields').value = cfg.payloadFields || 'Value, Timestamp';
        state.mqttConfigured = !!(cfg.enabled || (cfg.brokerUrl || '').trim());
    } catch (e) { /* ignore */ }
}
async function loadMqttStatus() {
    try {
        const st = await (await fetch('/api/mqtt/status', { cache: 'no-store' })).json();
        state.mqttState = st.state || 'Disconnected';
        state.mqttConnectionState = state.mqttState;
        if (el('mqttState')) {
            el('mqttState').textContent = st.state || 'Disconnected';
            el('mqttState').className = 'v ' + (st.state === 'Connected' ? 'badge good' : 'badge bad');
        }
        if (el('mqttLastError')) el('mqttLastError').textContent = st.lastError || 'No errors';
        if (el('mqttPublished')) el('mqttPublished').textContent = (st.publishedCount || 0).toLocaleString();
        if (el('mqttReceived')) el('mqttReceived').textContent = (st.receivedCount || 0).toLocaleString();
        if (el('mqttPublishedRate')) el('mqttPublishedRate').textContent = (st.publishedRate || 0).toFixed(1) + '/s';
        if (el('mqttReceivedRate')) el('mqttReceivedRate').textContent = (st.receivedRate || 0).toFixed(1) + '/s';
        const hintMqtt = el('hintMqtt');
        if (hintMqtt) {
            const off = !state.mqttConfigured || state.mqttState === 'Disconnected';
            const hasMqttTags = (state.mappings || []).some(m => (m.mqttEnabled ?? m.MqttEnabled) === true);
            hintMqtt.style.display = (off && hasMqttTags) ? '' : 'none';
            if (off && hasMqttTags) hintMqtt.innerHTML = 'MQTT tags exist but broker is disconnected.';
        }
    } catch (e) { if (el('mqttMessage')) el('mqttMessage').textContent = '✗ ' + e.message; }
}
async function loadMqtt() { await Promise.all([loadMqttConfig(), loadMqttStatus()]); }
async function saveMqtt() {
    const body = {
        enabled: el('mqttEnabled').checked,
        brokerUrl: el('mqttBrokerUrl').value.trim(),
        clientId: el('mqttClientId').value.trim(),
        userName: el('mqttUser').value.trim() || null,
        password: el('mqttPass').value || null,
        tls: el('mqttTls').checked,
        ignoreCertErrors: el('mqttIgnoreCert').checked,
        topicPrefix: el('mqttPrefix').value.trim(),
        payloadFields: el('mqttFields').value.trim()
    };
    const r = await fetch('/api/mqtt/config', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const p = await r.json();
    el('mqttMessage').textContent = p.status === 'ok' ? 'MQTT config saved.' : ('✗ ' + (p.error || 'save failed'));
    await loadMqtt();
}
async function connectMqtt() {
    el('mqttMessage').textContent = 'Connecting...';
    const r = await fetch('/api/mqtt/connect', { method: 'POST' });
    const p = await r.json();
    el('mqttMessage').textContent = p.status === 'ok' ? 'Connected.' : ('✗ ' + (p.error || 'connect failed'));
    await loadMqtt();
}
let wzMqttStepCur = 1;
const WZ_MQTT_STEPS = 3;

async function openMqttWizard() {
  wzMqttStepCur = 1;
  await loadMqtt();
  el('wzMqttUrl').value = el('mqttBrokerUrl').value || 'tcp://localhost:1883';
  el('wzMqttClientId').value = el('mqttClientId').value || 'OpcBridge';
  el('wzMqttAuto').checked = el('mqttEnabled').checked;
  el('wzMqttUser').value = el('mqttUser').value;
  el('wzMqttPass').value = el('mqttPass').value;
  el('wzMqttTls').checked = el('mqttTls').checked;
  el('wzMqttPrefix').value = el('mqttPrefix').value || 'bridge/tags';
  el('wzMqttFields').value = el('mqttFields').value;
  el('wzMqttConnectNow').checked = true;
  el('mqttWizard').classList.add('open');
  wzMqttRender();
}
function closeMqttWizard() { el('mqttWizard').classList.remove('open'); }
function wzMqttRender() {
  document.querySelectorAll('#mqttWizard .wizard-pane').forEach(p => p.classList.toggle('active', Number(p.dataset.pane) === wzMqttStepCur));
  document.querySelectorAll('#mqttWizard .wizard-step').forEach(s => {
    const n = Number(s.dataset.step);
    s.classList.toggle('active', n === wzMqttStepCur);
    s.classList.toggle('done', n < wzMqttStepCur);
  });
  el('wzMqttBack').style.display = wzMqttStepCur > 1 ? '' : 'none';
  el('wzMqttNext').style.display = wzMqttStepCur < WZ_MQTT_STEPS ? '' : 'none';
  el('wzMqttFinish').style.display = wzMqttStepCur === WZ_MQTT_STEPS ? '' : 'none';
  if (wzMqttStepCur === 3) {
    el('wzMqttSummary').innerHTML =
      `<b>Broker:</b> ${esc(el('wzMqttUrl').value)}<br>` +
      `<b>Client ID:</b> ${esc(el('wzMqttClientId').value)}<br>` +
      `<b>Auth:</b> ${el('wzMqttUser').value ? 'yes' : 'none'}<br>` +
      `<b>TLS:</b> ${el('wzMqttTls').checked ? 'on' : 'off'}<br>` +
      `<b>Topic Prefix:</b> ${esc(el('wzMqttPrefix').value)}<br>` +
      `<b>Auto-connect:</b> ${el('wzMqttAuto').checked ? 'on' : 'off'}`;
  }
}
function wzMqttStep(delta) {
  const next = wzMqttStepCur + delta;
  if (next < 1 || next > WZ_MQTT_STEPS) return;
  if (delta > 0 && !wzMqttValidate(wzMqttStepCur)) return;
  wzMqttStepCur = next;
  wzMqttRender();
}
function wzMqttValidate(step) {
  if (step === 1 && !el('wzMqttUrl').value.trim()) { alert('Broker URL is required.'); return false; }
  return true;
}
async function wzMqttFinish() {
  el('mqttBrokerUrl').value = el('wzMqttUrl').value.trim();
  el('mqttClientId').value = el('wzMqttClientId').value.trim() || 'OpcBridge';
  el('mqttEnabled').checked = el('wzMqttAuto').checked;
  el('mqttUser').value = el('wzMqttUser').value;
  el('mqttPass').value = el('wzMqttPass').value;
  el('mqttTls').checked = el('wzMqttTls').checked;
  el('mqttPrefix').value = el('wzMqttPrefix').value.trim() || 'bridge/tags';
  el('mqttFields').value = el('wzMqttFields').value;
  try {
    await saveMqtt();
    if (el('wzMqttConnectNow').checked) await connectMqtt();
    closeMqttWizard();
  } catch (e) {
    el('mqttMessage').textContent = '✗ ' + e.message;
  }
}
async function disconnectMqtt() {
    await fetch('/api/mqtt/disconnect', { method: 'POST' });
    el('mqttMessage').textContent = 'Disconnected.';
    await loadMqtt();
}
async function loadMqttValues() {
    try {
        state.mqttValFilter = {
            direction: (el('mqttValDir')?.value || '').trim(),
            topic: (el('mqttValTopic')?.value || '').trim()
        };
        const q = new URLSearchParams();
        if (state.mqttValFilter.direction) q.set('direction', state.mqttValFilter.direction);
        if (state.mqttValFilter.topic) q.set('topic', state.mqttValFilter.topic);
        q.set('pageSize', '100000');
        const p = await (await fetch('/api/mqtt/values?' + q.toString(), { cache: 'no-store' })).json();
        const items = p.items || [];
        renderMqttValues(items);
    } catch (e) { el('mqttTraffic').innerHTML = '<span class="msg">✗ ' + esc(e.message) + '</span>'; }
}
function renderMqttValues(items) {
    if (!items.length) { el('mqttTraffic').innerHTML = '<span class="msg">No MQTT tags yet.</span>'; return; }
    el('mqttTraffic').innerHTML = items.map(e =>
        `<div class="li"><span class="badge ${e.direction === 'PUB' ? 'good' : 'partial'}">${esc(e.direction)}</span>` +
        `<span class="mono">${esc(e.topic)}</span>` +
        `<span class="p">${esc(e.value || '')}</span>` +
        `<span class="s">${esc(new Date(e.timestampUtc).toLocaleTimeString())}</span></div>`).join('');
}
function onMqttValFilterChange() { loadMqttValues().catch(() => {}); }
let mqttValTopicTimer;
function onMqttValTopicInput() {
    clearTimeout(mqttValTopicTimer);
    mqttValTopicTimer = setTimeout(() => loadMqttValues().catch(() => {}), 250);
}
async function loadInfluxConfig() {
    try {
        const cfg = await (await fetch('/api/influx/config', { cache: 'no-store' })).json();
        if (el('influxEnabled')) el('influxEnabled').checked = !!cfg.enabled;
        if (el('influxUrl')) el('influxUrl').value = cfg.url || '';
        if (el('influxOrg')) el('influxOrg').value = cfg.org || '';
        if (el('influxBucket')) el('influxBucket').value = cfg.bucket || '';
        if (el('influxToken')) el('influxToken').value = cfg.token || '';
        if (el('influxMeasurement')) el('influxMeasurement').value = cfg.measurement || 'opc_tags';
        if (el('influxTimeoutMs')) el('influxTimeoutMs').value = String(cfg.timeoutMs ?? 5000);
        if (el('influxVerifySsl')) el('influxVerifySsl').checked = cfg.verifySsl !== false;
        state.influxConfigured = !!(cfg.enabled || (cfg.url || '').trim() || (cfg.org || '').trim() || (cfg.bucket || '').trim() || (cfg.token || '').trim());
    } catch (e) { /* ignore */ }
}
async function loadInfluxStatus() {
    try {
        const st = await (await fetch('/api/influx/status', { cache: 'no-store' })).json();
        state.influxState = st.state || 'Disconnected';
        if (el('influxState')) {
            el('influxState').textContent = st.state || 'Disconnected';
            el('influxState').className = 'v ' + (st.state === 'Connected' ? 'badge good' : 'badge bad');
        }
        if (el('influxLastError')) el('influxLastError').textContent = st.lastError || 'No errors';
        if (el('influxWritten')) el('influxWritten').textContent = (st.writtenCount || 0).toLocaleString();
        if (el('influxWrittenRate')) el('influxWrittenRate').textContent = (st.writtenRate || 0).toFixed(1) + '/s';
        const hintInflux = el('hintInflux');
        if (hintInflux) {
            const off = !state.influxConfigured || state.influxState === 'Disconnected';
            hintInflux.style.display = off ? '' : 'none';
            if (off) hintInflux.innerHTML = 'Historian (InfluxDB) not configured. <button class="btn" type="button" onclick="openInfluxWizard()">Configure</button>';
        }
    } catch (e) { if (el('influxMessage')) el('influxMessage').textContent = '✗ ' + e.message; }
}
async function loadInflux() { await Promise.all([loadInfluxConfig(), loadInfluxStatus()]); }
async function saveInflux() {
    const body = {
        enabled: el('influxEnabled').checked,
        url: el('influxUrl').value.trim(),
        org: el('influxOrg').value.trim(),
        bucket: el('influxBucket').value.trim(),
        token: el('influxToken').value || null,
        measurement: el('influxMeasurement').value.trim(),
        timeoutMs: Number(el('influxTimeoutMs').value) || 5000,
        verifySsl: el('influxVerifySsl').checked
    };
    const r = await fetch('/api/influx/config', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const p = await r.json();
    el('influxMessage').textContent = p.status === 'ok' ? 'Influx config saved.' : ('✗ ' + (p.error || 'save failed'));
    await loadInflux();
}
async function connectInflux() {
    el('influxMessage').textContent = 'Connecting...';
    const r = await fetch('/api/influx/connect', { method: 'POST' });
    const p = await r.json();
    el('influxMessage').textContent = p.status === 'ok' ? 'Connected.' : ('✗ ' + (p.error || 'connect failed'));
    await loadInflux();
}
let wzInfluxStepCur = 1;
const WZ_INFLUX_STEPS = 3;

async function openInfluxWizard() {
  wzInfluxStepCur = 1;
  await loadInflux();
  el('wzInfluxUrl').value = el('influxUrl').value || 'http://localhost:8086';
  el('wzInfluxOrg').value = el('influxOrg').value;
  el('wzInfluxBucket').value = el('influxBucket').value;
  el('wzInfluxToken').value = el('influxToken').value;
  el('wzInfluxAuto').checked = el('influxEnabled').checked;
  el('wzInfluxConnectNow').checked = true;
  el('influxWizard').classList.add('open');
  wzInfluxRender();
}
function closeInfluxWizard() { el('influxWizard').classList.remove('open'); }
function wzInfluxRender() {
  document.querySelectorAll('#influxWizard .wizard-pane').forEach(p => p.classList.toggle('active', Number(p.dataset.pane) === wzInfluxStepCur));
  document.querySelectorAll('#influxWizard .wizard-step').forEach(s => {
    const n = Number(s.dataset.step);
    s.classList.toggle('active', n === wzInfluxStepCur);
    s.classList.toggle('done', n < wzInfluxStepCur);
  });
  el('wzInfluxBack').style.display = wzInfluxStepCur > 1 ? '' : 'none';
  el('wzInfluxNext').style.display = wzInfluxStepCur < WZ_INFLUX_STEPS ? '' : 'none';
  el('wzInfluxFinish').style.display = wzInfluxStepCur === WZ_INFLUX_STEPS ? '' : 'none';
  if (wzInfluxStepCur === 3) {
    el('wzInfluxSummary').innerHTML =
      `<b>URL:</b> ${esc(el('wzInfluxUrl').value)}<br>` +
      `<b>Org:</b> ${esc(el('wzInfluxOrg').value || '—')}<br>` +
      `<b>Bucket:</b> ${esc(el('wzInfluxBucket').value || '—')}<br>` +
      `<b>Token:</b> ${el('wzInfluxToken').value ? 'set' : '—'}<br>` +
      `<b>Auto-connect:</b> ${el('wzInfluxAuto').checked ? 'on' : 'off'}`;
  }
}
function wzInfluxStep(delta) {
  const next = wzInfluxStepCur + delta;
  if (next < 1 || next > WZ_INFLUX_STEPS) return;
  if (delta > 0 && !wzInfluxValidate(wzInfluxStepCur)) return;
  wzInfluxStepCur = next;
  wzInfluxRender();
}
function wzInfluxValidate(step) {
  if (step === 1 && !el('wzInfluxUrl').value.trim()) { alert('URL is required.'); return false; }
  if (step === 2 && !el('wzInfluxToken').value) { alert('Token is required.'); return false; }
  return true;
}
async function wzInfluxFinish() {
  el('influxUrl').value = el('wzInfluxUrl').value.trim();
  el('influxOrg').value = el('wzInfluxOrg').value.trim();
  el('influxBucket').value = el('wzInfluxBucket').value.trim();
  el('influxToken').value = el('wzInfluxToken').value;
  el('influxEnabled').checked = el('wzInfluxAuto').checked;
  try {
    await saveInflux();
    if (el('wzInfluxConnectNow').checked) await connectInflux();
    closeInfluxWizard();
  } catch (e) {
    el('influxMessage').textContent = '✗ ' + e.message;
  }
}

async function disconnectInflux() {
    await fetch('/api/influx/disconnect', { method: 'POST' });
    el('influxMessage').textContent = 'Disconnected.';
    await loadInflux();
}
function applyMappingView(mappings) {
    const filter = (state.mappingFilter || '').trim().toLowerCase();
    let view = mappingsForMapType(mappings);
    if (filter) {
        view = view.filter(m => {
            const sourceId = m.sourceId || m.SourceId || 'default';
            const item = m.itemId || m.ItemId || m.daItemId || m.DaItemId || '';
            const name = m.displayName || m.DisplayName || item;
            const node = m.uaNodeId || m.UaNodeId || defaultUaNodeId(sourceId, item);
            return [sourceId, item, name, node, m.description || m.Description || ''].some(v => String(v).toLowerCase().includes(filter));
        });
    }
    const key = state.mappingSort;
    const dir = state.mappingSortDir;
    const accessRank = m => {
        const enabled = (m.enabled ?? m.Enabled) !== false;
        if (!enabled) return 0;
        const mode = m.mode || m.Mode || 'Source';
        if (mode === 'Manual') return 1;
        return ((m.writeable ?? m.Writeable) === true) ? 2 : 3;
    };
    const cmp = (a, b) => {
        let av, bv;
        switch (key) {
            case 'source': av = (a.sourceId || a.SourceId || 'default'); bv = (b.sourceId || b.SourceId || 'default'); break;
            case 'item': av = (a.itemId || a.ItemId || a.daItemId || a.DaItemId || ''); bv = (b.itemId || b.ItemId || b.daItemId || b.DaItemId || ''); break;
            case 'node': av = (a.uaNodeId || a.UaNodeId || ''); bv = (b.uaNodeId || b.UaNodeId || ''); break;
            case 'access': av = accessRank(a); bv = accessRank(b); break;
            case 'rate': av = (a.pollRateMs ?? a.PollRateMs ?? 0); bv = (b.pollRateMs ?? b.PollRateMs ?? 0); break;
            case 'deadband': av = Number(a.deadbandPct ?? a.DeadbandPct ?? 0); bv = Number(b.deadbandPct ?? b.DeadbandPct ?? 0); break;
            case 'status': av = ((a.enabled ?? a.Enabled) !== false) ? 0 : 1; bv = ((b.enabled ?? b.Enabled) !== false) ? 0 : 1; break;
            case 'description': av = (a.description || a.Description || ''); bv = (b.description || b.Description || ''); break;
            default: av = (a.displayName || a.DisplayName || a.itemId || a.ItemId || a.daItemId || a.DaItemId || ''); bv = (b.displayName || b.DisplayName || b.itemId || b.ItemId || b.daItemId || b.DaItemId || '');
        }
        if (typeof av === 'number' && typeof bv === 'number') return (av - bv) * dir;
        return String(av).localeCompare(String(bv), undefined, { numeric: true, sensitivity: 'base' }) * dir;
    };
    return view.slice().sort(cmp);
}
function updateNoMappingsBanner() {
    const bannerNoMap = el('bannerNoMappings');
    if (bannerNoMap) {
        const typed = mappingsForMapType(state.mappings || []);
        const noMappings = typed.length === 0;
        bannerNoMap.style.display = (noMappings && (state.sources || []).length > 0) ? '' : 'none';
        if (noMappings && (state.sources || []).length > 0) bannerNoMap.innerHTML = 'No tags mapped yet. <button class="btn" type="button" onclick="navigate(\'tags/maps\')">Map Tags</button>';
    }
}
function rerenderMappings() {
    const typed = mappingsForMapType(state.mappings || []);
    const view = applyMappingView(state.mappings || []);
    if (el('mapCount')) el('mapCount').textContent = view.length + (view.length !== typed.length ? ' / ' + typed.length + ' mappings' : ' mappings');
    if (el('mappedList')) el('mappedList').innerHTML = renderMappingRows(view);
    updateNoMappingsBanner();
}

function getMapping(sourceId, itemId) {
    return state.mappings.find(mapping => {
        const mappingSourceId = mapping.sourceId || mapping.SourceId || 'default';
        const mappingItemId = mapping.itemId || mapping.ItemId || mapping.daItemId || mapping.DaItemId;
        return mappingSourceId === sourceId && mappingItemId === itemId;
    }) || null;
}



async function updateMapping(sourceId, itemId, mutate) {
    const mapping = getMapping(sourceId, itemId);
    if (!mapping) throw new Error('Mapping not found.');
    const payload = {
        sourceId,
        itemId: itemId,
        displayName: mapping.displayName || mapping.DisplayName || itemId,
        description: mapping.description ?? mapping.Description ?? null,
        dataType: mapping.dataType || mapping.DataType || 'Auto',
        uaNodeId: mapping.uaNodeId || mapping.UaNodeId || defaultUaNodeId(sourceId, itemId),
        enabled: (mapping.enabled ?? mapping.Enabled) !== false,
        mode: mapping.mode || mapping.Mode || 'Source',
        manualValue: mapping.manualValue ?? mapping.ManualValue ?? null,
        pollRateMs: mapping.pollRateMs ?? mapping.PollRateMs ?? 0,
        daGroup: mapping.daGroup ?? mapping.DaGroup ?? null,
        subscription: mapping.subscription ?? mapping.Subscription ?? '',
        deadbandPct: Number(mapping.deadbandPct ?? mapping.DeadbandPct ?? 0),
        writeable: (mapping.writeable ?? mapping.Writeable) === true,
        accessRights: mapping.accessRights || mapping.AccessRights || 'Read',
        mqttEnabled: el('fpMqttEnabled').checked,
        mqttTopic: el('fpMqttTopic').value.trim() || null,
        influxEnabled: el('fpInfluxEnabled').checked
    };
    mutate(payload);
    const r = await fetch('/api/mappings/update', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ tag: payload })
    });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    await loadMappings();
    await refresh();
    el('mappingMessage').textContent = 'Mapping updated.';
}

function pickSource(sourceId, opts) {
    state.selectedSourceId = sourceId;
    state.editingNewSource = false;
    state.editingNewUaSource = false;
    state.tagPath = '';
    state.uaBrowseTrail = [];
    el('tagTree').innerHTML = '<span class="msg">Browse the active source to load tags.</span>';
    el('tagStatus').textContent = 'Browse all tags, or open folders one level at a time.';
    renderCrumb();
    renderSources();
    if (document.getElementById('view-interlinks')?.classList.contains('active')) renderInterlinksView();
    if (opts && opts.openConfig) {
        const src = state.sources.find(s => s.sourceId === sourceId);
        if (src && isUaSource(src)) navigate('connectivity/opc-ua');
        else navigate('connectivity/opc-da');
    }
}
async function saveSource() {
    const sourceId = el('cfgSourceId').value.trim();
    if (!sourceId) {
        el('cfgMessage').textContent = '✗ Source ID is required.';
        return;
    }
    const existing = state.sources.find(s => s.sourceId === sourceId);
    if (existing && isDriverSource(existing)) {
        el('cfgMessage').textContent = 'Serial driver source — edit it on the Drivers page.';
        return;
    }
    if (existing && isMxSource(existing)) {
        el('cfgMessage').textContent = 'MX Component source — edit it on the MX Component page.';
        return;
    }
    const body = {
        sourceId,
        displayName: el('cfgDisplayName').value.trim() || null,
        sourceType: 'OpcDa',
        progId: el('cfgProgId').value.trim(),
        host: el('cfgHost').value.trim() || 'localhost',
        remoteUsername: el('cfgUser').value.trim() || null,
        remotePassword: el('cfgPass').value || null,
        remoteDomain: el('cfgDomain').value.trim() || null,
        ioMode: el('cfgIoMode') ? el('cfgIoMode').value : undefined
    };
    const r = await fetch('/api/da/sources', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    state.selectedSourceId = p.source?.sourceId || body.sourceId;
    state.editingNewSource = false;
    await loadSources();
    await refresh();
    el('cfgMessage').textContent = 'Source saved.';
    hideSaveReset();
}
async function loadGroupsSection() {
    const container = el('cfgGroups');
    if (!container) return;
    const source = currentSource();
    if (!source || isUaSource(source) || isMelsecSource(source) || isS7Source(source) || isMxSource(source)) {
        container.innerHTML = '';
        const msg = el('cfgGroupsMsg'); if (msg) msg.textContent = '';
        return;
    }
    try {
        const r = await fetch('/api/da/sources/groups?sourceId=' + encodeURIComponent(source.sourceId));
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const data = await r.json();
        renderGroups((data.groups || []), source.sourceId, data.sourceIoMode);
        const msg = el('cfgGroupsMsg'); if (msg) msg.textContent = '';
    } catch (e) {
        container.innerHTML = '';
        const msg = el('cfgGroupsMsg'); if (msg) msg.textContent = 'Failed to load rate groups: ' + e.message;
    }
}
function renderGroups(groups, sourceId, sourceIoMode) {
    // Read-only summary — editing lives in the DA Groups panel.
    const container = el('cfgGroups');
    if (!container) return;
    let html = '<div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin-bottom:6px">' +
        '<span style="font-weight:600;font-size:12px">' + groups.length + ' group(s)</span>' +
        '<button class="btn ghost" type="button" style="height:20px;padding:0 8px;font-size:11px" onclick="navigate(\'connectivity/opc-da-groups\')">Manage groups</button>' +
        '</div>';
    if (!groups.length) {
        html += '<span class="msg">No groups yet.</span>';
    } else {
        html += '<div style="display:flex;gap:5px;flex-wrap:wrap">';
        for (const g of groups) {
            html += '<span class="dag-badge' + (g.isDefault ? ' accent' : '') + '">' +
                esc(g.name) + ' · ' + esc(daPrettyRate(g.rate)) + ' · ' + esc(prettyIoMode(g.ioMode || 'AutoDetect')) +
                (g.isDefault ? ' · default' : '') + '</span>';
        }
        html += '</div>';
    }
    container.innerHTML = html;
}
function prettyIoMode(mode) {
    if (mode === 'Sync') return 'Synchronous I/O';
    if (mode === 'Async20') return 'Async I/O 2.0';
    return 'AutoDetect I/O';
}
async function loadDaGroupsTab() {
    const container = el('daGroupsContainer');
    const msg = el('daGroupsMsg');
    if (!container) return;
    container.innerHTML = '<div class="hint">Loading…</div>';
    if (msg) msg.textContent = '';
    try {
        const opcDaSrcs = state.sources.filter(s => !isUaSource(s) && !isDriverSource(s) && !isMxSource(s));
        if (opcDaSrcs.length === 0) {
            container.innerHTML = '<div class="hint">No OPC DA sources — add one in <a href="#/connectivity/opc-da" onclick="navigate(\'connectivity/opc-da\');return false;">OPC DA</a>.</div>';
            return;
        }
        container.innerHTML = '';
        for (const src of opcDaSrcs) {
            const card = document.createElement('div');
            card.className = 'box';
            card.style.cssText = 'margin-bottom:8px';
            const header = document.createElement('div');
            header.className = 'box-h';
            header.style.cssText = 'padding:6px 10px;font-size:12px;gap:6px;cursor:pointer;user-select:none';
            const bodyId = 'daGroupsBody-' + src.sourceId;
            const isCollapsed = (state.collapsedDaGroups || {})[src.sourceId];
            header.innerHTML = '<span class="toggle" style="width:14px;text-align:center;font-size:10px;opacity:.6">' + (isCollapsed ? '▶' : '▼') + '</span><span class="dag-src-name">' + esc(src.displayName || src.sourceId) + '</span> <span class="msg dag-src-meta">' + esc(src.sourceId) + ' · ' + esc(src.progId || '') + '</span><span class="msg dag-src-host">' + esc(src.host || 'localhost') + '</span><button class="btn ghost" type="button" style="height:20px;padding:0 8px;font-size:11px" onclick="event.stopPropagation();openDagAdd(\'' + esc(src.sourceId).replace(/'/g, "\'") + '\')">+ Add Group</button>';
            const body = document.createElement('div');
            body.className = 'box-b';
            body.id = bodyId;
            body.style.cssText = 'padding:8px 10px' + (isCollapsed ? ';display:none' : '');
            header.onclick = () => {
                const b = document.getElementById(bodyId);
                const t = header.querySelector('.toggle');
                if (!b) return;
                const collapsed = b.style.display === 'none';
                b.style.display = collapsed ? '' : 'none';
                if (t) t.textContent = collapsed ? '▼' : '▶';
                state.collapsedDaGroups = state.collapsedDaGroups || {};
                state.collapsedDaGroups[src.sourceId] = !collapsed;
            };
            body.innerHTML = '<div class="hint" id="daGroupsHint-' + esc(src.sourceId) + '" style="font-size:11px;margin-bottom:4px">Loading groups…</div><div id="daGroupsTable-' + esc(src.sourceId) + '"></div>';
            card.appendChild(header);
            card.appendChild(body);
            container.appendChild(card);
            await reloadDaGroups(src.sourceId);
        }
    } catch (e) {
        if (msg) msg.textContent = 'Failed to load: ' + e.message;
    }
}
function setDaGroupsStatus(t) {
    const m = document.getElementById('daGroupsMsg');
    if (m) m.textContent = t;
}
async function reloadDaGroups(sourceId) {
    // Sequenced per source: whichever request started latest wins; superseded
    // responses are dropped instead of painting stale data over newer.
    state.daGroupRenderSeq = state.daGroupRenderSeq || {};
    const seq = (state.daGroupRenderSeq[sourceId] || 0) + 1;
    state.daGroupRenderSeq[sourceId] = seq;
    try {
        const r = await fetch('/api/da/sources/groups?sourceId=' + encodeURIComponent(sourceId));
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const data = await r.json();
        if (state.daGroupRenderSeq[sourceId] !== seq) return false;
        renderDaGroupsForSource(sourceId, data.groups || [], data.sourceIoMode);
        return true;
    } catch (e) {
        if (state.daGroupRenderSeq[sourceId] === seq) {
            const hint = document.getElementById('daGroupsHint-' + sourceId);
            if (hint) hint.textContent = 'Failed to load groups: ' + e.message;
        }
        return false;
    }
}
function daPrettyRate(ms) {
    const n = Number(ms) || 0;
    return n >= 1000 ? (n / 1000) + ' s' : n + ' ms';
}
function ensureDaGroupsCache(sourceId) {
    // Named DA groups per source, for the faceplate UPDATE RATE selector.
    state.daGroupsCache = state.daGroupsCache || {};
    if (state.daGroupsCache[sourceId]) return Promise.resolve(state.daGroupsCache[sourceId]);
    return fetch('/api/da/sources/groups?sourceId=' + encodeURIComponent(sourceId))
        .then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
        .then(data => { state.daGroupsCache[sourceId] = data.groups || []; return state.daGroupsCache[sourceId]; });
}
function invalidateDaGroupsCache(sourceId) {
    if (state.daGroupsCache) delete state.daGroupsCache[sourceId];
}
function daGroupRateFor(sourceId, name) {
    const gs = (state.daGroupsCache || {})[sourceId] || [];
    const g = gs.find(x => String(x.name ?? '').toLowerCase() === String(name ?? '').toLowerCase());
    return g ? (Number(g.rate) || 0) : 0;
}
// UPDATE RATE selector: Source Default + one option per named group.
// Legacy numeric rates survive as synthetic "@<ms>" entries until touched.
function fpRateOptions(sourceId, selectedDaGroup, pollRateMs) {
    const gs = (state.daGroupsCache || {})[sourceId] || [];
    const want = String(selectedDaGroup || '').toLowerCase();
    let matched = false;
    let html = '';
    for (const g of gs) {
        const name = String(g.name ?? '');
        const sel = want !== '' && want === name.toLowerCase();
        if (sel) matched = true;
        html += '<option value="' + attr(name) + '"' + (sel ? ' selected' : '') + '>' + esc(name) + ' · ' + esc(daPrettyRate(g.rate)) + '</option>';
    }
    let defaultSelected = false;
    const rateN = Number(pollRateMs) || 0;
    if (!matched) {
        if (rateN > 0 && !gs.some(g => Number(g.rate) === rateN)) {
            html += '<option value="@' + rateN + '" selected>' + esc(daPrettyRate(rateN)) + ' (legacy rate)</option>';
        } else {
            defaultSelected = true;
        }
    }
    return '<option value=""' + (defaultSelected ? ' selected' : '') + '>Source Default</option>' + html;
}
// SUBSCRIPTION selector (OPC UA sources): Source Default (with the source's default
// rate label) plus one option per named subscription with its rate. Matching is
// case-insensitive, mirroring the server-side subscription-name comparisons.
function fpSubscriptionOptions(sourceId, selected) {
    const src = uaSubsFor(sourceId);
    const want = String(selected || '').trim().toLowerCase();
    let matched = false;
    let html = '';
    for (const sub of (src ? src.subscriptions : [])) {
        const sel = want !== '' && want === String(sub.name).toLowerCase();
        if (sel) matched = true;
        html += `<option value="${attr(sub.name)}"${sel ? ' selected' : ''}>${esc(sub.name)} (${formatMs(sub.updateRateMs)})</option>`;
    }
    if (want !== '' && !matched) {
        html += `<option value="${attr(selected)}" selected>${esc(selected)}</option>`;
    }
    const defRate = src ? src.defaultUpdateRateMs : ((state.sources.find(s => s.sourceId === sourceId) || {}).updateRateMs ?? 0);
    return `<option value=""${matched ? '' : ' selected'}>Source Default (${formatMs(defRate)})</option>` + html;
}
// A named subscription owns the tag's rate: lock the per-tag Update Rate input
// while a subscription is chosen, unlock when back on Source Default.
function updateFpRateEnabled() {
    const subSel = el('fpSubscription');
    const rate = el('fpPollRate');
    const hint = el('fpSubscriptionHint');
    if (!subSel || !rate) return;
    rate.disabled = !!subSel.value;
    if (hint) hint.textContent = subSel.value ? 'Rate comes from the named subscription.' : '';
}
function renderDaGroupsForSource(sourceId, groups, sourceIoMode) {
    const hint = document.getElementById('daGroupsHint-' + sourceId);
    const wrap = document.getElementById('daGroupsTable-' + sourceId);
    if (!wrap) return;
    if (!groups || groups.length === 0) {
        if (hint) { hint.textContent = 'No groups — default (' + prettyIoMode(sourceIoMode) + ')'; hint.style.cssText = 'font-size:11px'; }
        wrap.innerHTML = '';
        return;
    }
    if (hint) { hint.textContent = groups.length + ' group(s) — ' + prettyIoMode(sourceIoMode) + ' default'; hint.style.cssText = 'font-size:11px;margin-bottom:6px'; }
    const sidA = esc(sourceId).replace(/'/g, "\\'");
    let html = '<div class="dag-grid">';
    for (const g of groups) {
        const isDef = !!g.isDefault;
        const tags = g.tagCount ?? 0;
        html += '<div class="dag-card' + (isDef ? ' default' : '') + '" data-name="' + esc(g.name) + '" data-rate="' + esc(String(g.rate)) + '" data-io="' + esc(g.ioMode || 'AutoDetect') + '">' +
            '<div class="n">' + esc(g.name) + (isDef ? ' <span class="badge partial" style="font-size:10px;padding:0 7px">default</span>' : '') + '</div>' +
            '<div class="dag-badges">' +
                '<span class="dag-badge accent">' + esc(daPrettyRate(g.rate)) + '</span>' +
                '<span class="dag-badge">' + esc(prettyIoMode(g.ioMode || 'AutoDetect')) + '</span>' +
            '</div>' +
            '<div class="dag-meta">effective: ' + esc(prettyIoMode(g.effective)) + ' · ' + tags + ' tag' + (tags === 1 ? '' : 's') + '</div>' +
            '<div class="dag-actions">' + (isDef
                ? '<span class="msg">read-only</span>'
                : '<button class="btn ghost" type="button" onclick="openDagEdit(\'' + sidA + '\', this.closest(\'.dag-card\'))">Edit</button>' +
                  '<button class="btn ghost" type="button" onclick="deleteDaGroup(\'' + sidA + '\', \'' + esc(g.name).replace(/'/g, "\\'") + '\')">Delete</button>') +
            '</div>' +
        '</div>';
    }
    html += '</div>';
    wrap.innerHTML = html;
}
async function deleteDaGroup(sourceId, name) {
    if (!confirm('Delete group ' + name + ' for ' + sourceId + '?')) return;
    try {
        const r = await fetch('/api/da/sources/groups/reset', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceId, name }) });
        const p = await r.json();
        if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
        setDaGroupsStatus('Deleted ' + name);
        invalidateDaGroupsCache(sourceId);
        await reloadDaGroups(sourceId);
        refresh().catch(()=>{});
        loadGroupsSection().catch(()=>{});
    } catch (e) {
        setDaGroupsStatus('Delete failed: ' + e.message);
    }
}
let dagModalCtx = null;
function openDagAdd(sourceId) {
    dagModalCtx = { sourceId: sourceId, oldName: null };
    el('dagModalTitle').textContent = 'Add Group';
    el('dagModalSource').textContent = sourceId;
    el('dagModalName').value = 'OpcBridge_' + (Date.now() % 10000);
    el('dagModalName').disabled = false;
    el('dagModalRate').value = '1000';
    el('dagModalIo').value = 'AutoDetect';
    el('dagModalMsg').textContent = '';
    const b = el('dagModalSaveBtn'); if (b) b.disabled = false;
    const m = el('dagModal'); if (m) m.classList.add('open');
    setTimeout(() => { const n = el('dagModalName'); if (n) n.focus(); }, 50);
}
function openDagEdit(sourceId, card) {
    if (!card) return;
    dagModalCtx = { sourceId: sourceId, oldName: card.dataset.name };
    el('dagModalTitle').textContent = 'Edit Group';
    el('dagModalSource').textContent = sourceId;
    el('dagModalName').value = card.dataset.name || '';
    el('dagModalName').disabled = false;
    el('dagModalRate').value = String(card.dataset.rate || '1000');
    el('dagModalIo').value = card.dataset.io || 'AutoDetect';
    el('dagModalMsg').textContent = '';
    const b = el('dagModalSaveBtn'); if (b) b.disabled = false;
    const m = el('dagModal'); if (m) m.classList.add('open');
}
function closeDagModal() {
    const m = el('dagModal'); if (m) m.classList.remove('open');
    dagModalCtx = null;
}
async function dagModalSave() {
    if (!dagModalCtx) return;
    const sourceId = dagModalCtx.sourceId;
    const oldName = dagModalCtx.oldName;
    const name = el('dagModalName').value.trim();
    const rate = parseInt(el('dagModalRate').value, 10);
    const ioMode = el('dagModalIo').value;
    if (!name) { el('dagModalMsg').textContent = 'Name required'; return; }
    const saveBtn = el('dagModalSaveBtn');
    if (saveBtn) saveBtn.disabled = true;
    try {
        // rename => delete old entry first (name is the group key)
        if (oldName && oldName !== name) {
            const del = await fetch('/api/da/sources/groups/reset', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceId: sourceId, name: oldName }) });
            if (!del.ok) { const p = await del.json().catch(() => ({})); throw new Error(p.error || ('HTTP ' + del.status)); }
        }
        const r = await fetch('/api/da/sources/groups', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceId: sourceId, name: name, rate: rate, ioMode: ioMode, renameFrom: (oldName && oldName !== name) ? oldName : null }) });
        const p = await r.json();
        if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
        closeDagModal();
        invalidateDaGroupsCache(sourceId);
        await reloadDaGroups(sourceId);
        setDaGroupsStatus('Saved ' + name + ' (' + daPrettyRate(rate) + ') → ' + prettyIoMode(ioMode));
        refresh().catch(() => {});
        loadGroupsSection().catch(() => {});
    } catch (e) {
        el('dagModalMsg').textContent = 'Save failed: ' + e.message;
        if (saveBtn) saveBtn.disabled = false;
    }
}
function expandAllDaGroups() {
    state.collapsedDaGroups = {};
    document.querySelectorAll('[id^="daGroupsBody-"]').forEach(b => b.style.display = '');
    document.querySelectorAll('#daGroupsContainer .box-h .toggle').forEach(t => t.textContent = '▼');
}
function collapseAllDaGroups() {
    state.collapsedDaGroups = {};
    document.querySelectorAll('[id^="daGroupsBody-"]').forEach(b => {
        const sid = b.id.replace('daGroupsBody-', '');
        state.collapsedDaGroups[sid] = true;
        b.style.display = 'none';
    });
    document.querySelectorAll('#daGroupsContainer .box-h .toggle').forEach(t => t.textContent = '▶');
}
async function saveUpdateRate() {
    const updateRateMs = Number.parseInt(el('cfgUpdateRate').value, 10);
    if (!Number.isFinite(updateRateMs) || updateRateMs <= 0) {
        el('rateMessage').textContent = '✗ Select a rate.';
        return;
    }

    const r = await fetch('/api/da/update-rate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ updateRateMs })
    });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    state.updateRateMs = Number(p.updateRateMs || updateRateMs);
    el('cfgUpdateRate').value = String(state.updateRateMs);
    await refresh();
    el('rateMessage').textContent = 'Default rate applied: ' + state.updateRateMs + ' ms.';
}
async function removeSelectedSource() {
    const source = currentSource();
    if (!source || state.editingNewSource || isUaSource(source)) return;
    if (!confirm('Remove source "' + source.sourceId + '" and its source → OPC UA mappings?')) return;
    const r = await fetch('/api/da/sources/remove', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceId: source.sourceId }) });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    state.selectedSourceId = 'default';
    await loadSources();
    await loadMappings();
    await refresh();
    el('cfgMessage').textContent = 'Source removed.';
}
function showSaveReset() { el('cfgApply').style.display = ''; el('cfgReset').style.display = ''; }
function hideSaveReset() { el('cfgApply').style.display = 'none'; el('cfgReset').style.display = 'none'; }

// --- Named UA subscriptions (UA Subs tab) ---
// Group UA tags onto shared monitored items: GET lists per-source definitions plus
// live status; POST upserts; /remove deletes and moves its mappings back to default.
let uaSubsCache = [];
function setUaSubsStatus(t) {
    const m = document.getElementById('subsMsg');
    if (m) m.textContent = t;
}
async function loadUaSubs() {
    const container = el('uaSubsContainer');
    if (!container) return;
    const p = await (await fetch('/api/ua/subscriptions', { cache: 'no-store' })).json();
    uaSubsCache = p.sources || [];
    if (!uaSubsCache.length) {
        container.innerHTML = '<div class="hint">No OPC UA sources — add one in <a href="#/connectivity/opc-ua" onclick="navigate(\'connectivity/opc-ua\');return false;">OPC UA</a>.</div>';
        return;
    }
    let html = '';
    for (const s of uaSubsCache) {
        const collapsed = (state.collapsedUaSubs || {})[s.sourceId];
        const sidA = esc(s.sourceId).replace(/'/g, "\\'");
        html += '<div class="box" style="margin-bottom:8px">' +
            '<div class="box-h" style="padding:6px 10px;font-size:12px;gap:6px;cursor:pointer;user-select:none" onclick="toggleUaSubsCard(\'' + sidA + '\')">' +
                '<span class="toggle" style="width:14px;text-align:center;font-size:10px;opacity:.6" id="uaSubsToggle-' + attr(s.sourceId) + '">' + (collapsed ? '▶' : '▼') + '</span>' +
                '<span class="dag-src-name">' + esc(s.displayName || s.sourceId) + '</span>' +
                '<span class="msg dag-src-meta">' + esc(s.sourceId) + ' · default ' + esc(formatMs(s.defaultUpdateRateMs)) + '</span>' +
                '<button class="btn ghost" type="button" style="height:20px;padding:0 8px;font-size:11px;margin-left:auto" onclick="event.stopPropagation();openUaSubAdd(\'' + sidA + '\')">+ Add Subscription</button>' +
            '</div>' +
            '<div class="box-b" id="uaSubsBody-' + attr(s.sourceId) + '" style="padding:8px 10px' + (collapsed ? ';display:none' : '') + '">' +
                '<div class="hint" id="uaSubsHint-' + attr(s.sourceId) + '" style="font-size:11px;margin-bottom:6px">Loading…</div>' +
                '<div class="dag-grid" id="uaSubsGrid-' + attr(s.sourceId) + '"></div>' +
            '</div>' +
        '</div>';
    }
    container.innerHTML = html;
    for (const s of uaSubsCache) renderUaSubsForSource(s);
}
function toggleUaSubsCard(sourceId) {
    const b = document.getElementById('uaSubsBody-' + sourceId);
    if (!b) return;
    const t = document.getElementById('uaSubsToggle-' + sourceId);
    const collapsed = b.style.display === 'none';
    b.style.display = collapsed ? '' : 'none';
    if (t) t.textContent = collapsed ? '▼' : '▶';
    state.collapsedUaSubs = state.collapsedUaSubs || {};
    state.collapsedUaSubs[sourceId] = !collapsed;
}
function setAllUaSubsCollapsed(collapsed) {
    state.collapsedUaSubs = {};
    for (const s of uaSubsCache) {
        state.collapsedUaSubs[s.sourceId] = collapsed;
        const b = document.getElementById('uaSubsBody-' + s.sourceId);
        const t = document.getElementById('uaSubsToggle-' + s.sourceId);
        if (b) b.style.display = collapsed ? 'none' : '';
        if (t) t.textContent = collapsed ? '▶' : '▼';
    }
}
function expandAllUaSubs() { setAllUaSubsCollapsed(false); }
function collapseAllUaSubs() { setAllUaSubsCollapsed(true); }
function renderUaSubsForSource(s) {
    const hint = document.getElementById('uaSubsHint-' + s.sourceId);
    const grid = document.getElementById('uaSubsGrid-' + s.sourceId);
    if (!grid) return;
    const named = s.subscriptions || [];
    const d = s.defaultStats || {};
    const defTags = d.itemCount ?? 0;
    if (hint) hint.textContent = named.length + ' named subscription' + (named.length === 1 ? '' : 's') + ' · unassigned tags ride Default';
    const sidA = esc(s.sourceId).replace(/'/g, "\\'");
    let html = '';
    // Read-only Default tile — mirrors DA Groups' read-only default group card.
    html += '<div class="dag-card default">' +
        '<div class="n">default <span class="badge partial" style="font-size:10px;padding:0 7px">default</span></div>' +
        '<div class="dag-badges">' +
            '<span class="dag-badge accent">' + esc(formatMs(s.defaultUpdateRateMs)) + '</span>' +
            '<span class="dag-badge">' + (d.created ? 'live' : 'idle') + '</span>' +
        '</div>' +
        '<div class="dag-meta">actual: ' + esc(formatMs(Math.round(d.actualPublishingIntervalMs || 0))) + ' · ' + defTags + ' tag' + (defTags === 1 ? '' : 's') + '</div>' +
        '<div class="dag-actions"><span class="msg">read-only</span></div>' +
    '</div>';
    for (const sub of named) {
        const tags = sub.itemCount ?? 0;
        html += '<div class="dag-card" data-name="' + attr(sub.name) + '" data-rate="' + esc(String(sub.updateRateMs)) + '">' +
            '<div class="n">' + esc(sub.name) + '</div>' +
            '<div class="dag-badges">' +
                '<span class="dag-badge accent">' + esc(formatMs(sub.updateRateMs)) + '</span>' +
                '<span class="dag-badge">' + (sub.created ? 'live' : 'idle') + '</span>' +
            '</div>' +
            '<div class="dag-meta">actual: ' + esc(formatMs(Math.round(sub.actualPublishingIntervalMs || 0))) + ' · ' + tags + ' tag' + (tags === 1 ? '' : 's') + '</div>' +
            '<div class="dag-actions">' +
                '<button class="btn ghost" type="button" onclick="openUaSubEdit(\'' + sidA + '\', this.closest(\'.dag-card\'))">Edit</button>' +
                '<button class="btn ghost" type="button" onclick="deleteUaSub(\'' + sidA + '\', \'' + esc(sub.name).replace(/'/g, "\\'") + '\')">Delete</button>' +
            '</div>' +
        '</div>';
    }
    grid.innerHTML = html;
}
let uaSubModalCtx = null;
function uaSubsFor(sourceId) {
    return (uaSubsCache || []).find(s => String(s.sourceId).toLowerCase() === String(sourceId || '').toLowerCase()) || null;
}
function openUaSubAdd(sourceId) {
    uaSubModalCtx = { sourceId: sourceId, oldName: null };
    el('uaSubModalTitle').textContent = 'Add Subscription';
    el('uaSubModalSource').textContent = sourceId;
    el('uaSubModalName').value = '';
    el('uaSubModalName').disabled = false;
    el('uaSubModalRate').value = '1000';
    el('uaSubModalMsg').textContent = '';
    const b = el('uaSubModalSaveBtn'); if (b) b.disabled = false;
    const m = el('uaSubModal'); if (m) m.classList.add('open');
    setTimeout(() => { const n = el('uaSubModalName'); if (n) n.focus(); }, 50);
}
function openUaSubEdit(sourceId, card) {
    if (!card) return;
    uaSubModalCtx = { sourceId: sourceId, oldName: card.dataset.name };
    el('uaSubModalTitle').textContent = 'Edit Subscription';
    el('uaSubModalSource').textContent = sourceId;
    el('uaSubModalName').value = card.dataset.name || '';
    el('uaSubModalName').disabled = false;
    el('uaSubModalRate').value = String(card.dataset.rate || '1000');
    el('uaSubModalMsg').textContent = '';
    const b = el('uaSubModalSaveBtn'); if (b) b.disabled = false;
    const m = el('uaSubModal'); if (m) m.classList.add('open');
}
function closeUaSubModal() {
    const m = el('uaSubModal'); if (m) m.classList.remove('open');
    uaSubModalCtx = null;
}
// Rename support: /api/mappings/update replaces the whole mapping, so send each
// affected mapping back verbatim with only `subscription` swapped to the new name.
async function repointMappingsSubscription(sourceId, fromName, toName) {
    const data = await (await fetch('/api/mappings', { cache: 'no-store' })).json();
    const matches = (data.mappings || []).filter(m =>
        String(m.sourceId).toLowerCase() === String(sourceId).toLowerCase() &&
        String(m.subscription || '').trim().toLowerCase() === String(fromName).toLowerCase());
    for (const m of matches) {
        const r = await fetch('/api/mappings/update', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ tag: Object.assign({}, m, { subscription: toName }) })
        });
        if (!r.ok) throw new Error('Re-point failed for ' + (m.itemId || 'tag') + ' (HTTP ' + r.status + ')');
    }
    return matches.length;
}
async function uaSubModalSave() {
    if (!uaSubModalCtx) return;
    const sourceId = uaSubModalCtx.sourceId;
    const oldName = uaSubModalCtx.oldName;
    const name = el('uaSubModalName').value.trim();
    const updateRateMs = parseInt(el('uaSubModalRate').value, 10);
    if (!name) { el('uaSubModalMsg').textContent = 'Name required'; return; }
    if (!Number.isFinite(updateRateMs) || updateRateMs <= 0) { el('uaSubModalMsg').textContent = 'Rate must be positive'; return; }
    const saveBtn = el('uaSubModalSaveBtn');
    if (saveBtn) saveBtn.disabled = true;
    try {
        // Upsert by (case-insensitive) name; a casing-only change is an in-place rate edit.
        const renamed = !!oldName && oldName.toLowerCase() !== name.toLowerCase();
        const r = await fetch('/api/ua/subscriptions', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceId, name, updateRateMs }) });
        const p = await r.json().catch(() => ({}));
        if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
        if (renamed) {
            // Name is the bucket key: drop the old entry, then re-point its tags to
            // the new name via the mappings update API (remove alone lands them on default).
            const del = await fetch('/api/ua/subscriptions/remove', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceId, name: oldName }) });
            const dp = await del.json().catch(() => ({}));
            if (!del.ok) throw new Error(dp.error || ('HTTP ' + del.status));
            const moved = await repointMappingsSubscription(sourceId, oldName, name);
            setUaSubsStatus('✓ Renamed ' + oldName + ' → ' + name + (moved > 0 ? ' — ' + moved + ' tag(s) re-pointed.' : ''));
        } else {
            setUaSubsStatus('✓ Saved ' + name + ' (' + formatMs(updateRateMs) + ').');
        }
        closeUaSubModal();
        await loadUaSubs();
        await loadMappings().catch(() => {});
    } catch (e) {
        el('uaSubModalMsg').textContent = '✗ ' + e.message;
        const btn = el('uaSubModalSaveBtn'); if (btn) btn.disabled = false;
    }
}
async function deleteUaSub(sourceId, name) {
    if (!confirm('Delete subscription ' + name + ' for ' + sourceId + '? Its tags move back to default.')) return;
    try {
        const r = await fetch('/api/ua/subscriptions/remove', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceId, name }) });
        const p = await r.json().catch(() => ({}));
        if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
        setUaSubsStatus('Deleted ' + name + '. ' + (p.movedMappings ?? 0) + ' tag(s) moved to default.');
        await loadUaSubs();
        // Reassigned tags changed their effective rate/pill — refresh Maps rows.
        await loadMappings().catch(() => {});
    } catch (e) {
        setUaSubsStatus('✗ Delete failed: ' + e.message);
    }
}

// --- PLC driver sources (Melsec A3N) ---
function mxSources() { return state.sources.filter(s => isMxSource(s)); }
function currentMx() { return state.editingNewMx ? null : mxSources().find(s => s.sourceId === state.selectedMxId) || null; }
function renderMx() {
    const sources = mxSources();
    if (!state.editingNewMx && !sources.some(s => s.sourceId === state.selectedMxId)) {
        state.selectedMxId = sources.length ? sources[0].sourceId : '';
    }
    el('mxCount').textContent = sources.length + ' connection' + (sources.length !== 1 ? 's' : '');
    el('mxList').innerHTML = sources.length ? sources.map(source =>
        `<div class="li source-row"><div><div class="n">${esc(source.displayName || source.sourceId)} ${sourceTypeBadge(source)}</div><div class="p">${esc(source.sourceId)} · MX station ${esc(String(source.logicalStationNumber ?? 0))} · ${formatMs(source.updateRateMs)}</div></div><button class="btn ghost" data-action="select-mx" data-source-id="${attr(source.sourceId)}">Select</button></div>`
    ).join('') : '<span class="msg">No MX Component connections configured. Click + Add Connection.</span>';
    loadMxForm();
}
function pickMxSource(sourceId) {
    state.selectedMxId = sourceId;
    state.editingNewMx = false;
    renderMx();
}
function loadMxForm() {
    const source = currentMx();
    if (!source) return;
    state.editingNewMx = false;
    el('mxSourceId').value = source.sourceId || '';
    el('mxSourceId').disabled = true;
    el('mxName').value = source.displayName || '';
    el('mxStation').value = String(source.logicalStationNumber ?? 0);
    el('mxTimeout').value = String(source.timeoutMs || 3000);
    el('mxRetry').value = String(source.retryCount ?? 2);
    el('mxRate').value = String(source.updateRateMs || 1000);
    el('mxMaxTags').value = String(source.maxMappedTags || 2000);
    el('mxMessage').textContent = 'Editing ' + (source.displayName || source.sourceId) + '.';
}
function newMxSource() {
    state.selectedMxId = '';
    state.editingNewMx = true;
    el('mxSourceId').disabled = false;
    el('mxSourceId').value = '';
    el('mxName').value = '';
    el('mxStation').value = '0';
    el('mxTimeout').value = '3000';
    el('mxRetry').value = '2';
    el('mxRate').value = '1000';
    el('mxMaxTags').value = '2000';
    el('mxMessage').textContent = 'Enter a unique Source ID and the logical station number assigned in MX Component.';
}
function resetMx() {
    if (state.editingNewMx) { newMxSource(); return; }
    loadMxForm();
    el('mxMessage').textContent = 'Reverted to saved values.';
}
function mxFormBody() {
    return {
        sourceId: el('mxSourceId').value.trim(),
        displayName: el('mxName').value.trim() || null,
        sourceType: 'MxComponent',
        logicalStationNumber: Number(el('mxStation').value) || 0,
        timeoutMs: Number(el('mxTimeout').value) || 3000,
        retryCount: Number(el('mxRetry').value) || 0,
        maxMappedTags: Number(el('mxMaxTags').value) || 2000,
        updateRateMs: Number(el('mxRate').value) || 1000
    };
}
async function saveMxSource() {
    const body = mxFormBody();
    if (!body.sourceId) {
        el('mxMessage').textContent = '✗ Source ID is required.';
        return false;
    }
    const r = await fetch('/api/da/sources', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    state.selectedMxId = p.source?.sourceId || body.sourceId;
    state.editingNewMx = false;
    await loadSources();
    await refresh();
    renderMx();
    el('mxMessage').textContent = 'MX Component connection saved.';
    return true;
}
async function removeMxSource() {
    const source = currentMx();
    if (!source || state.editingNewMx) return;
    if (!confirm('Remove MX Component connection "' + source.sourceId + '" and its tag mappings?')) return;
    const r = await fetch('/api/da/sources/remove', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceId: source.sourceId }) });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    state.selectedMxId = '';
    await loadSources();
    await loadMappings();
    await refresh();
    renderMx();
    el('mxMessage').textContent = 'MX Component connection removed.';
}
async function testMxConnection() {
    el('mxMessage').textContent = 'Testing connection…';
    const r = await fetch('/api/drivers/mx-component/test-connection', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(mxFormBody()) });
    const p = await r.json();
    el('mxMessage').textContent = p.ok ? '✓ Connection OK — MX Component opened the station.' : '✗ ' + (p.error || ('HTTP ' + r.status));
}
function driverSources() { return state.sources.filter(s => isDriverSource(s)); }
function currentDriver() { return state.editingNewDriver ? null : driverSources().find(s => s.sourceId === state.selectedDriverId) || null; }
function renderDrivers() {
    const drivers = driverSources();
    if (!state.editingNewDriver && !drivers.some(s => s.sourceId === state.selectedDriverId)) {
        state.selectedDriverId = drivers.length ? drivers[0].sourceId : '';
    }
    el('drvA3nCount').textContent = drivers.length + ' driver' + (drivers.length !== 1 ? 's' : '');
    el('drvA3nList').innerHTML = drivers.length ? drivers.map(source => {
        const detail = `${esc(source.sourceId)} · ${esc(source.serialPortName || '?')} @ ${esc(String(source.baudRate || ''))} · ${formatMs(source.updateRateMs)}`;
        return `<div class="li source-row"><div><div class="n">${esc(source.displayName || source.sourceId)} ${sourceTypeBadge(source)}</div><div class="p">${detail}</div></div><button class="btn ghost" data-action="select-driver" data-source-id="${attr(source.sourceId)}">Select</button></div>`;
    }).join('') : '<span class="msg">No driver sources configured. Click + Add Driver.</span>';
    loadDriverForm();
}
function loadDriverForm() {
    const source = currentDriver();
    if (!source) return;
    state.editingNewDriver = false;
    el('drvA3nSourceId').value = source.sourceId || '';
    el('drvA3nSourceId').disabled = true;
    el('drvA3nName').value = source.displayName || '';
    el('drvA3nPort').value = source.serialPortName || '';
    el('drvA3nBaud').value = String(source.baudRate || 9600);
    el('drvA3nDataBits').value = String(source.dataBits || 8);
    el('drvA3nParity').value = source.parity || 'Odd';
    el('drvA3nStopBits').value = source.stopBits || 'One';
    el('drvA3nStation').value = source.stationNo || '00';
    el('drvA3nPc').value = source.pcNo || 'FF';
    el('drvA3nTimeout').value = String(source.timeoutMs || 3000);
    el('drvA3nRetry').value = String(source.retryCount ?? 2);
    el('drvA3nRate').value = String(source.updateRateMs || 1000);
    el('drvA3nMaxTags').value = String(source.maxMappedTags || 2000);
    el('drvA3nMessage').textContent = 'Editing ' + (source.displayName || source.sourceId) + '.';
    if (isS7Source(source)) {
        if (el('drvS7LocalPpi')) el('drvS7LocalPpi').value = String(source.localPpiAddress ?? 0);
        if (el('drvS7RemotePpi')) el('drvS7RemotePpi').value = String(source.remotePpiAddress ?? 2);
        setDriverFormType('S7200Ppi');
        el('drvA3nParity').value = source.parity || 'Even';
    } else {
        setDriverFormType('MelsecA3n');
    }
}
function pickDriver(sourceId) {
    state.selectedDriverId = sourceId;
    state.editingNewDriver = false;
    renderDrivers();
}
function newDriver() {
    state.selectedDriverId = '';
    state.editingNewDriver = true;
    el('drvA3nSourceId').disabled = false;
    el('drvA3nSourceId').value = '';
    el('drvA3nName').value = '';
    el('drvA3nPort').value = '';
    el('drvA3nBaud').value = '9600';
    el('drvA3nDataBits').value = '8';
    el('drvA3nParity').value = 'Odd';
    el('drvA3nStopBits').value = 'One';
    el('drvA3nStation').value = '00';
    el('drvA3nPc').value = 'FF';
    el('drvA3nTimeout').value = '3000';
    el('drvA3nRetry').value = '2';
    el('drvA3nRate').value = '1000';
    el('drvA3nMaxTags').value = '2000';
    setDriverFormType('MelsecA3n');
    el('drvA3nMessage').textContent = 'Enter a unique Source ID and serial port, then save.';
}
function resetDriver() {
    if (state.editingNewDriver) { newDriver(); return; }
    loadDriverForm();
    el('drvA3nMessage').textContent = 'Reverted to saved values.';
}
function driverFormBody() {
    const type = state.driverFormType || 'MelsecA3n';
    const body = {
        sourceId: el('drvA3nSourceId').value.trim(),
        displayName: el('drvA3nName').value.trim() || null,
        sourceType: type,
        transport: 'Serial',
        serialPortName: el('drvA3nPort').value.trim(),
        baudRate: Number(el('drvA3nBaud').value) || 9600,
        dataBits: Number(el('drvA3nDataBits').value) || 8,
        parity: el('drvA3nParity').value,
        stopBits: el('drvA3nStopBits').value,
        timeoutMs: Number(el('drvA3nTimeout').value) || 3000,
        retryCount: Number(el('drvA3nRetry').value) || 0,
        maxMappedTags: Number(el('drvA3nMaxTags').value) || 2000,
        updateRateMs: Number(el('drvA3nRate').value) || 1000
    };
    if (type === 'S7200Ppi') {
        body.localPpiAddress = Number(el('drvS7LocalPpi')?.value ?? 0);
        body.remotePpiAddress = Number(el('drvS7RemotePpi')?.value ?? 2);
        if (Number.isNaN(body.localPpiAddress)) body.localPpiAddress = 0;
        if (Number.isNaN(body.remotePpiAddress)) body.remotePpiAddress = 2;
    } else {
        body.stationNo = el('drvA3nStation').value.trim() || '00';
        body.pcNo = el('drvA3nPc').value.trim() || 'FF';
    }
    return body;
}
async function saveDriverSource() {
    const body = driverFormBody();
    if (!body.sourceId) {
        el('drvA3nMessage').textContent = '✗ Source ID is required.';
        return false;
    }
    if (!body.serialPortName) {
        el('drvA3nMessage').textContent = '✗ Serial port is required.';
        return false;
    }
    const r = await fetch('/api/da/sources', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    state.selectedDriverId = p.source?.sourceId || body.sourceId;
    state.editingNewDriver = false;
    await loadSources();
    await refresh();
    renderDrivers();
    el('drvA3nMessage').textContent = 'Driver source saved.';
    return true;
}
async function removeDriver() {
    const source = currentDriver();
    if (!source || state.editingNewDriver) return;
    if (!confirm('Remove driver source "' + source.sourceId + '" and its tag mappings?')) return;
    const r = await fetch('/api/da/sources/remove', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceId: source.sourceId }) });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    state.selectedDriverId = '';
    await loadSources();
    await loadMappings();
    await refresh();
    renderDrivers();
    el('drvA3nMessage').textContent = 'Driver source removed.';
}
async function testDriverConnection() {
    const body = driverFormBody();
    if (!body.serialPortName) {
        el('drvA3nMessage').textContent = '✗ Serial port is required.';
        return;
    }
    el('drvA3nMessage').textContent = 'Testing connection…';
    const url = body.sourceType === 'S7200Ppi' ? '/api/drivers/s7200-ppi/test-connection' : '/api/drivers/melsec-a3n/test-connection';
    const r = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const p = await r.json();
    el('drvA3nMessage').textContent = p.ok ? '✓ Connection OK — PLC responded.' : '✗ ' + (p.error || ('HTTP ' + r.status));
}
async function scanSerialPorts(targetInputId, listId, msgId) {
    const msg = el(msgId);
    const list = el(listId);
    if (msg) msg.textContent = 'Scanning…';
    if (list) list.innerHTML = '';
    try {
        const r = await fetch('/api/serial/ports', { cache: 'no-store' });
        const p = await r.json().catch(() => ({}));
        if (!r.ok) {
            if (msg) msg.textContent = '✗ ' + (p.error || ('HTTP ' + r.status));
            return;
        }
        if (p.error && msg) msg.textContent = '✗ ' + p.error;
        const ports = p.ports || [];
        if (list) {
            list.innerHTML = ports.length
                ? ports.map(port => `<div class="li"><div style="flex:1"><div class="n mono">${esc(port)}</div></div><button class="btn ghost" data-action="use-serial-port" data-port="${esc(port)}" data-target="${esc(targetInputId)}" type="button">Use</button></div>`).join('')
                : '<span class="msg">No serial ports found.</span>';
        }
        if (msg && !p.error) {
            msg.textContent = ports.length
                ? (ports.length + ' port' + (ports.length === 1 ? '' : 's') + ' found. Click Use to fill Port.')
                : 'No serial ports found on this host.';
        }
    } catch (e) {
        if (msg) msg.textContent = '✗ ' + e.message;
        if (list) list.innerHTML = '<span class="msg">Scan failed.</span>';
    }
}
function useSerialPort(port, targetInputId) {
    if (!port || !targetInputId) return;
    const input = el(targetInputId);
    if (!input) return;
    input.value = port;
    if (targetInputId === 'drvA3nPort') {
        el('drvA3nMessage').textContent = 'Selected ' + port + ' — save or Test connection.';
    }
}
let wzDrvCurrentStep = 1;
const WZDRV_STEPS = 5;
function openDriverWizard() {
    wzDrvCurrentStep = 1;
    ['wzDrvSourceId','wzDrvName','wzDrvPort'].forEach(id => el(id).value = '');
    el('wzDrvType').value = 'MelsecA3n';
    wzDrvOnTypeChange();
    el('wzDrvBaud').value = '9600';
    el('wzDrvDataBits').value = '8';
    el('wzDrvParity').value = 'Odd';
    el('wzDrvStopBits').value = 'One';
    el('wzDrvStation').value = '00';
    el('wzDrvPc').value = 'FF';
    el('wzDrvTimeout').value = '3000';
    el('wzDrvRetry').value = '2';
    el('wzDrvRate').value = '1000';
    el('wzDrvMaxTags').value = '2000';
    if (el('listWzDrvPorts')) el('listWzDrvPorts').innerHTML = '';
    if (el('msgWzDrvPorts')) el('msgWzDrvPorts').textContent = 'Click Scan to list host serial ports.';
    el('wzDrv').classList.add('open');
    wzDrvRender();
}
function closeDriverWizard() { el('wzDrv').classList.remove('open'); }
function wzDrvRender() {
    document.querySelectorAll('.wzdrv-pane').forEach(p => p.classList.toggle('active', Number(p.dataset.pane) === wzDrvCurrentStep));
    document.querySelectorAll('.wzdrv-step').forEach(s => {
        const n = Number(s.dataset.step);
        s.classList.toggle('active', n === wzDrvCurrentStep);
        s.classList.toggle('done', n < wzDrvCurrentStep);
    });
    el('wzDrvBack').style.display = wzDrvCurrentStep > 1 ? '' : 'none';
    el('wzDrvNext').style.display = wzDrvCurrentStep < WZDRV_STEPS ? '' : 'none';
    el('wzDrvFinish').style.display = wzDrvCurrentStep === WZDRV_STEPS ? '' : 'none';
    if (wzDrvCurrentStep === WZDRV_STEPS) wzDrvBuildSummary();
}
function wzDrvStep(delta) {
    const next = wzDrvCurrentStep + delta;
    if (next < 1 || next > WZDRV_STEPS) return;
    if (delta > 0 && !wzDrvValidate(wzDrvCurrentStep)) return;
    wzDrvCurrentStep = next;
    wzDrvRender();
}
function wzDrvValidate(step) {
    if (step === 2) {
        const id = el('wzDrvSourceId').value.trim();
        if (!id) { alert('Source ID is required.'); return false; }
        if (/\s/.test(id)) { alert('Source ID must not contain spaces.'); return false; }
        if (state.sources.some(s => s.sourceId === id)) { alert('Source ID already exists.'); return false; }
    }
    if (step === 3 && !el('wzDrvPort').value.trim()) { alert('Serial port is required.'); return false; }
    return true;
}
function wzDrvBuildSummary() {
    const s7 = el('wzDrvType').value === 'S7200Ppi';
    const typeLabel = s7 ? 'Siemens S7-200 (PPI serial)' : 'Mitsubishi Melsec A3N (serial 1C)';
    const serialLine = `<b>Serial:</b> ${esc(el('wzDrvPort').value)} @ ${el('wzDrvBaud').value} baud, ${el('wzDrvDataBits').value}${el('wzDrvParity').value[0]}${el('wzDrvStopBits').value === 'Two' ? '2' : '1'}<br>`;
    const addrLine = s7
        ? `<b>Local / Remote PPI:</b> ${esc(el('wzDrvLocalPpi').value || '0')} / ${esc(el('wzDrvRemotePpi').value || '2')}<br>`
        : `<b>Station / PC:</b> ${esc(el('wzDrvStation').value || '00')} / ${esc(el('wzDrvPc').value || 'FF')}<br>`;
    el('wzDrvSummary').innerHTML =
        `<b>Type:</b> ${typeLabel}<br>` +
        `<b>Source ID:</b> ${esc(el('wzDrvSourceId').value)}<br>` +
        `<b>Display Name:</b> ${esc(el('wzDrvName').value || '—')}<br>` +
        serialLine +
        addrLine +
        `<b>Timeout:</b> ${el('wzDrvTimeout').value} ms · <b>Retries:</b> ${el('wzDrvRetry').value}<br>` +
        `<b>Update Rate:</b> ${el('wzDrvRate').value} ms · <b>Max tags:</b> ${el('wzDrvMaxTags').value}`;
}
async function wzDrvFinish() {
    el('drvA3nSourceId').disabled = false;
    el('drvA3nSourceId').value = el('wzDrvSourceId').value.trim();
    el('drvA3nName').value = el('wzDrvName').value.trim();
    el('drvA3nPort').value = el('wzDrvPort').value.trim();
    el('drvA3nBaud').value = el('wzDrvBaud').value;
    el('drvA3nDataBits').value = el('wzDrvDataBits').value;
    el('drvA3nParity').value = el('wzDrvParity').value;
    el('drvA3nStopBits').value = el('wzDrvStopBits').value;
    el('drvA3nStation').value = el('wzDrvStation').value;
    el('drvA3nPc').value = el('wzDrvPc').value;
    if (el('drvS7LocalPpi') && el('wzDrvLocalPpi')) el('drvS7LocalPpi').value = el('wzDrvLocalPpi').value;
    if (el('drvS7RemotePpi') && el('wzDrvRemotePpi')) el('drvS7RemotePpi').value = el('wzDrvRemotePpi').value;
    setDriverFormType(el('wzDrvType').value || 'MelsecA3n');
    el('drvA3nTimeout').value = el('wzDrvTimeout').value;
    el('drvA3nRetry').value = el('wzDrvRetry').value;
    el('drvA3nRate').value = el('wzDrvRate').value;
    el('drvA3nMaxTags').value = el('wzDrvMaxTags').value;
    state.editingNewDriver = true;
    try {
        const saved = await saveDriverSource();
        if (!saved) return;
        closeDriverWizard();
        if (confirm('Driver source saved. Map tags now?')) navigate('tags/maps');
    } catch (e) {
        el('drvA3nMessage').textContent = '✗ ' + e.message;
    }
}
function resetSource() {
    if (state.editingNewSource) { newSource(); return; }
    loadSelectedSourceForm();
    el('cfgMessage').textContent = 'Reverted to saved values.';
}

function newSource() {
    state.selectedSourceId = '';
    state.editingNewSource = true;
    state.editingNewUaSource = false;
    el('selectedSource').value = '';
    el('mapSourceSelect').value = '';
    el('cfgSourceId').disabled = false;
    el('cfgSourceId').value = '';
    el('cfgDisplayName').value = '';
    el('cfgProgId').value = '';
    el('cfgHost').value = 'localhost';
    el('cfgUser').value = '';
    el('cfgPass').value = '';
    el('cfgDomain').value = '';
    el('tagTree').innerHTML = '<span class="msg">Save the new source before browsing tags.</span>';
    el('cfgMessage').textContent = 'Enter a unique Source ID, then save.';
    updateCfgServerInfo(null);
    showSaveReset();
}
function showUaSaveReset() { el('uaCfgApply').style.display = ''; el('uaCfgReset').style.display = ''; }
function hideUaSaveReset() { el('uaCfgApply').style.display = 'none'; el('uaCfgReset').style.display = 'none'; }
function newUaSource() {
    state.selectedSourceId = '';
    state.editingNewUaSource = true;
    state.editingNewSource = false;
    if (el('uaSelectedSource')) el('uaSelectedSource').value = '';
    el('uaCfgSourceId').disabled = false;
    el('uaCfgSourceId').value = '';
    el('uaCfgDisplayName').value = '';
    el('uaCfgEndpointUrl').value = '';
    el('uaCfgSecurityMode').value = 'None';
    el('uaCfgSecurityPolicy').value = 'None';
    el('uaCfgUser').value = '';
    el('uaCfgPass').value = '';
    el('uaCfgUpdateRate').value = String(state.updateRateMs || 1000);
    el('uaCfgMaxMappedTags').value = '50000';
    el('uaCfgUseSubscriptions').checked = true;
    el('uaCfgMessage').textContent = 'Enter a unique Source ID and endpoint, then save.';
    showUaSaveReset();
}
function resetUaSource() {
    if (state.editingNewUaSource) { newUaSource(); return; }
    loadSelectedUaSourceForm();
    el('uaCfgMessage').textContent = 'Reverted to saved values.';
}
async function saveUaSource() {
    const sourceId = el('uaCfgSourceId').value.trim();
    if (!sourceId) {
        el('uaCfgMessage').textContent = '✗ Source ID is required.';
        return;
    }
    const endpointUrl = el('uaCfgEndpointUrl').value.trim();
    if (!endpointUrl) {
        el('uaCfgMessage').textContent = '✗ Endpoint URL is required.';
        return;
    }
    const body = {
        sourceId,
        displayName: el('uaCfgDisplayName').value.trim() || null,
        sourceType: 'OpcUa',
        endpointUrl,
        securityMode: el('uaCfgSecurityMode').value,
        securityPolicy: el('uaCfgSecurityPolicy').value,
        uaUsername: el('uaCfgUser').value.trim() || null,
        uaPassword: el('uaCfgPass').value || null,
        updateRateMs: parseInt(el('uaCfgUpdateRate').value, 10) || 1000,
        maxMappedTags: parseInt(el('uaCfgMaxMappedTags').value, 10) || 50000,
        useSubscriptions: el('uaCfgUseSubscriptions').checked,
        progId: '',
        host: ''
    };
    const r = await fetch('/api/da/sources', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    state.selectedSourceId = p.source?.sourceId || body.sourceId;
    state.editingNewUaSource = false;
    await loadSources();
    await refresh();
    el('uaCfgMessage').textContent = 'Source saved.';
    hideUaSaveReset();
}
async function testUaConnection() {
    const body = {
        endpointUrl: el('uaCfgEndpointUrl').value.trim(),
        securityMode: el('uaCfgSecurityMode').value,
        securityPolicy: el('uaCfgSecurityPolicy').value,
        username: el('uaCfgUser').value.trim() || null,
        password: el('uaCfgPass').value || null,
        sourceId: el('uaCfgSourceId').value.trim() || null
    };
    if (!body.endpointUrl) {
        el('uaCfgMessage').textContent = '✗ Endpoint URL is required.';
        return;
    }
    el('uaCfgMessage').textContent = 'Testing connection…';
    const r = await fetch('/api/ua/test-connection', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const p = await r.json().catch(() => ({}));
    if (!r.ok || p.ok === false) {
        el('uaCfgMessage').textContent = '✗ ' + (p.error || p.message || ('HTTP ' + r.status));
        return;
    }
    const bits = [];
    if (p.serverProductName || p.productName) bits.push(p.serverProductName || p.productName);
    if (p.sessionId) bits.push('session ' + p.sessionId);
    el('uaCfgMessage').textContent = '✓ Connected' + (bits.length ? ' — ' + bits.join(' · ') : '.');
}
async function discoverUaServers() {
    const discoveryUrl = (el('uaDiscoverUrl').value.trim()
        || el('uaCfgEndpointUrl').value.trim()
        || 'opc.tcp://localhost:4840');
    el('msgUaDiscover').textContent = 'Scanning…';
    el('listUaDiscover').innerHTML = '';
    const body = {
        endpointUrl: discoveryUrl,
        securityMode: el('uaCfgSecurityMode').value || 'None',
        securityPolicy: el('uaCfgSecurityPolicy').value || 'None',
        username: el('uaCfgUser').value.trim() || null,
        password: el('uaCfgPass').value || null
    };
    try {
        const r = await fetch('/api/ua/discover', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), cache: 'no-store' });
        const p = await r.json().catch(() => ({}));
        if (!r.ok || p.ok === false) {
            el('msgUaDiscover').textContent = '✗ ' + (p.error || p.message || ('HTTP ' + r.status));
            el('listUaDiscover').innerHTML = '<span class="msg">No servers found.</span>';
            return;
        }
        const servers = p.servers || [];
        el('listUaDiscover').innerHTML = servers.length ? servers.map(s => {
            const name = s.serverName || s.serverUri || s.discoveryUrl || '(unnamed)';
            const url = s.discoveryUrl || '';
            const caps = (s.serverCapabilities && s.serverCapabilities.length) ? s.serverCapabilities.join(', ') : '';
            const sub = [url, caps].filter(Boolean).join(' · ');
            return `<div class="li"><div style="flex:1"><div class="n">${esc(name)}</div><div class="p">${esc(sub)}</div></div><button class="btn ghost" data-action="pick-ua-server" data-url="${attr(url)}" data-name="${attr(name)}">Use</button></div>`;
        }).join('') : '<span class="msg">No servers found.</span>';
        el('msgUaDiscover').textContent = servers.length + ' server' + (servers.length === 1 ? '' : 's') + ' at ' + discoveryUrl;
    } catch (e) {
        el('msgUaDiscover').textContent = '✗ ' + e.message;
        el('listUaDiscover').innerHTML = '<span class="msg">Scan failed.</span>';
    }
}
function pickUaServer(url, name) {
    if (!url) return;
    el('uaCfgEndpointUrl').value = url;
    if (name && !el('uaCfgDisplayName').value.trim()) el('uaCfgDisplayName').value = name;
    el('uaCfgMessage').textContent = 'Selected ' + (name || url) + ' — save source to apply.';
    showUaSaveReset();
}
async function removeSelectedUaSource() {
    const source = currentSource();
    if (!source || !isUaSource(source) || state.editingNewUaSource) return;
    if (!confirm('Remove source "' + source.sourceId + '" and its mappings?')) return;
    const r = await fetch('/api/da/sources/remove', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceId: source.sourceId }) });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    state.selectedSourceId = 'default';
    await loadSources();
    await loadMappings();
    await refresh();
    el('uaCfgMessage').textContent = 'Source removed.';
}
let wzCurrentStep = 1;
const WZ_STEPS = 6;
function wzIsUa() { return (el('wzSourceType')?.value || 'OpcDa') === 'OpcUa'; }
function wzOnTypeChange() {
  const ua = wzIsUa();
  const daServer = el('wzDaServerFields');
  const uaServer = el('wzUaServerFields');
  const daAuth = el('wzDaAuthFields');
  const uaAuth = el('wzUaAuthFields');
  const maxTags = el('wzMaxTagsField');
  if (daServer) daServer.style.display = ua ? 'none' : '';
  if (uaServer) uaServer.style.display = ua ? '' : 'none';
  if (daAuth) daAuth.style.display = ua ? 'none' : '';
  if (uaAuth) uaAuth.style.display = ua ? '' : 'none';
  if (maxTags) maxTags.style.display = ua ? '' : 'none';
  const hint = el('wzSubsHint');
  if (hint) hint.textContent = ua ? 'Use MonitoredItems for mapped tags (recommended)' : 'Use IOPCDataCallback (recommended)';
}
function openAddSourceWizard() {
  wzCurrentStep = 1;
  ['wzSourceId','wzDisplayName','wzHost','wzProgId','wzDomain','wzUser','wzPass','wzEndpointUrl','wzUaUser','wzUaPass'].forEach(id => { const n = el(id); if (n) n.value = ''; });
  if (el('wzSourceType')) el('wzSourceType').value = 'OpcDa';
  el('wzHost').value = 'localhost';
  el('wzSubs').checked = true;
  el('wzUpdateRate').value = '1000';
  if (el('wzSecurityMode')) el('wzSecurityMode').value = 'None';
  if (el('wzSecurityPolicy')) el('wzSecurityPolicy').value = 'None';
  if (el('wzMaxMappedTags')) el('wzMaxMappedTags').value = '50000';
  el('wzListServers').innerHTML = '';
  el('wzMsgServers').textContent = '';
  wzOnTypeChange();
  el('addSourceWizard').classList.add('open');
  wzRender();
}
function closeAddSourceWizard() { el('addSourceWizard').classList.remove('open'); }
function wzRender() {
  document.querySelectorAll('.wizard-pane').forEach(p => p.classList.toggle('active', Number(p.dataset.pane) === wzCurrentStep));
  document.querySelectorAll('.wizard-step').forEach(s => {
    const n = Number(s.dataset.step);
    s.classList.toggle('active', n === wzCurrentStep);
    s.classList.toggle('done', n < wzCurrentStep);
  });
  el('wzBack').style.display = wzCurrentStep > 1 ? '' : 'none';
  el('wzNext').style.display = wzCurrentStep < WZ_STEPS ? '' : 'none';
  el('wzFinish').style.display = wzCurrentStep === WZ_STEPS ? '' : 'none';
  if (wzCurrentStep === 3 || wzCurrentStep === 4 || wzCurrentStep === 5) wzOnTypeChange();
  if (wzCurrentStep === WZ_STEPS) wzBuildSummary();
}
function wzStep(delta) {
  const next = wzCurrentStep + delta;
  if (next < 1 || next > WZ_STEPS) return;
  if (delta > 0 && !wzValidate(wzCurrentStep)) return;
  wzCurrentStep = next;
  wzRender();
}
function wzValidate(step) {
  if (step === 2) {
    const id = el('wzSourceId').value.trim();
    if (!id) { alert('Source ID is required.'); return false; }
    if (/\s/.test(id)) { alert('Source ID must not contain spaces.'); return false; }
    if (state.sources.some(s => s.sourceId === id)) { alert('Source ID already exists.'); return false; }
  }
  if (step === 3) {
    if (wzIsUa()) {
      if (!el('wzEndpointUrl').value.trim()) { alert('Endpoint URL is required.'); return false; }
    } else if (!el('wzProgId').value.trim()) {
      alert('ProgID / CLSID is required.'); return false;
    }
  }
  return true;
}
async function wzBrowseServers() {
  const host = el('wzHost').value.trim() || 'localhost';
  el('wzMsgServers').textContent = 'Scanning…';
  const body = { host: host === 'localhost' ? null : host };
  try {
    const r = await fetch('/api/da/servers', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), cache: 'no-store' });
    const p = await r.json();
    if (p.error) throw new Error(p.error);
    const servers = p.servers || [];
    el('wzListServers').innerHTML = servers.length ? servers.map(s => {
      const prog = s.progId || s.ProgId;
      const desc = s.description || s.Description || prog;
      return `<div class="li"><div style="flex:1"><div class="n">${esc(desc)}</div><div class="p">${esc(prog)}</div></div><button class="btn ghost" data-action="wz-pick-server" data-prog-id="${attr(prog)}" data-host="${attr(host)}">Use</button></div>`;
    }).join('') : '<span class="msg">No servers found.</span>';
    el('wzMsgServers').textContent = servers.length + ' servers';
  } catch (e) { el('wzMsgServers').textContent = '✗ ' + e.message; }
}
function wzPickServer(progId, host) {
  el('wzProgId').value = progId;
  el('wzHost').value = host;
  el('wzMsgServers').textContent = 'Selected ' + progId;
}
function wzBuildSummary() {
  if (wzIsUa()) {
    el('wzSummary').innerHTML =
      `<b>Type:</b> OPC UA<br>` +
      `<b>Source ID:</b> ${esc(el('wzSourceId').value)}<br>` +
      `<b>Display Name:</b> ${esc(el('wzDisplayName').value || '—')}<br>` +
      `<b>Endpoint:</b> ${esc(el('wzEndpointUrl').value)}<br>` +
      `<b>Security:</b> ${esc(el('wzSecurityMode').value)} / ${esc(el('wzSecurityPolicy').value)}<br>` +
      `<b>Credentials:</b> ${el('wzUaUser').value ? esc(el('wzUaUser').value) : 'anonymous'}<br>` +
      `<b>Update Rate:</b> ${el('wzUpdateRate').value} ms<br>` +
      `<b>Subscriptions:</b> ${el('wzSubs').checked ? 'on' : 'off'}<br>` +
      `<b>Max Mapped Tags:</b> ${esc(el('wzMaxMappedTags').value || '50000')}`;
  } else {
    el('wzSummary').innerHTML =
      `<b>Type:</b> OPC DA<br>` +
      `<b>Source ID:</b> ${esc(el('wzSourceId').value)}<br>` +
      `<b>Display Name:</b> ${esc(el('wzDisplayName').value || '—')}<br>` +
      `<b>Host:</b> ${esc(el('wzHost').value || 'localhost')}<br>` +
      `<b>ProgID:</b> ${esc(el('wzProgId').value)}<br>` +
      `<b>Credentials:</b> ${el('wzUser').value ? el('wzDomain').value + '\\' + el('wzUser').value : 'none'}<br>` +
      `<b>Update Rate:</b> ${el('wzUpdateRate').value} ms<br>` +
      `<b>Subscriptions:</b> ${el('wzSubs').checked ? 'on' : 'off'}`;
  }
}
async function wzFinish() {
  if (wzIsUa()) {
    el('uaCfgSourceId').disabled = false;
    el('uaCfgSourceId').value = el('wzSourceId').value.trim();
    el('uaCfgDisplayName').value = el('wzDisplayName').value.trim();
    el('uaCfgEndpointUrl').value = el('wzEndpointUrl').value.trim();
    el('uaCfgSecurityMode').value = el('wzSecurityMode').value;
    el('uaCfgSecurityPolicy').value = el('wzSecurityPolicy').value;
    el('uaCfgUser').value = el('wzUaUser').value.trim();
    el('uaCfgPass').value = el('wzUaPass').value;
    el('uaCfgUpdateRate').value = el('wzUpdateRate').value;
    el('uaCfgMaxMappedTags').value = el('wzMaxMappedTags').value || '50000';
    el('uaCfgUseSubscriptions').checked = el('wzSubs').checked;
    state.editingNewUaSource = true;
    try {
      await saveUaSource();
      closeAddSourceWizard();
      navigate('connectivity/opc-ua');
      if (confirm('Source saved. Map tags now?')) navigate('tags/maps');
    } catch (e) {
      el('uaCfgMessage').textContent = '✗ ' + e.message;
    }
    return;
  }
  el('cfgSourceId').value = el('wzSourceId').value.trim();
  el('cfgDisplayName').value = el('wzDisplayName').value.trim();
  el('cfgProgId').value = el('wzProgId').value.trim();
  el('cfgHost').value = el('wzHost').value.trim() || 'localhost';
  el('cfgUser').value = el('wzUser').value.trim();
  el('cfgPass').value = el('wzPass').value;
  el('cfgDomain').value = el('wzDomain').value.trim();
  if (el('cfgIoMode')) el('cfgIoMode').value = el('wzSubs').checked ? 'AutoDetect' : 'Sync';
  state.editingNewSource = true;
  try {
    await saveSource();
    closeAddSourceWizard();
    navigate('connectivity/opc-da');
    if (confirm('Source saved. Map tags now?')) navigate('tags/maps');
  } catch (e) {
    el('cfgMessage').textContent = '✗ ' + e.message;
  }
}
async function browseServers() {
    const host = (el('cfgHost').value.trim() || 'localhost');
    el('msgServers').textContent = 'Scanning…';
    const user = el('cfgUser').value.trim();
    const pass = el('cfgPass').value;
    const domain = el('cfgDomain').value.trim();
    const body = { host: host === 'localhost' ? null : host };
    if (user) { body.username = user; body.password = pass; body.domain = domain || null; }
    const r = await fetch('/api/da/servers', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), cache: 'no-store' });
    const p = await r.json();
    if (p.error) throw new Error(p.error);
    const servers = p.servers || [];
    el('listServers').innerHTML = servers.length ? servers.map((s, i) => {
        const prog = s.progId || s.ProgId;
        const desc = s.description || s.Description || prog;
        return `<div class="li"><div style="flex:1"><div class="n">${esc(desc)}</div><div class="p">${esc(prog)}</div></div><button class="btn ghost" data-action="pick-server" data-prog-id="${attr(prog)}" data-host="${attr(host)}">Use</button></div>`;
    }).join('') : '<span class="msg">No servers found.</span>';
    el('msgServers').textContent = servers.length + ' servers' + (user ? ' (as ' + esc(domain || host) + '\\' + esc(user) + ')' : '');
}
function pickServer(progId, host) {
    el('cfgProgId').value = progId;
    el('cfgHost').value = host;
    el('cfgMessage').textContent = 'Selected server; save source to apply.';
}
function renderCrumb() {
    const bc = el('tagBreadcrumb');
    if (!state.tagPath) {
        bc.innerHTML = '<span class="current">root</span>';
        return;
    }
    const parts = state.tagPath.split('.');
    let html = '<a data-crumb="">root</a><span class="sep">/</span>';
    let acc = '';
    for (let i = 0; i < parts.length; i++) {
        acc = acc ? acc + '.' + parts[i] : parts[i];
        if (i < parts.length - 1) {
            html += `<a data-crumb="${attr(acc)}">${esc(parts[i])}</a><span class="sep">/</span>`;
        } else {
            html += `<span class="current">${esc(parts[i])}</span>`;
        }
    }
    bc.innerHTML = html;
}
function renderUaCrumb() {
    const bc = el('tagBreadcrumb');
    const trail = state.uaBrowseTrail || [];
    if (!trail.length) {
        bc.innerHTML = '<span class="current">root (Objects)</span>';
        return;
    }
    let html = '<a data-crumb="">root</a><span class="sep">/</span>';
    for (let i = 0; i < trail.length; i++) {
        const step = trail[i];
        if (i < trail.length - 1) {
            html += `<a data-crumb="${attr(step.nodeId)}" data-crumb-depth="${i}">${esc(step.name)}</a><span class="sep">/</span>`;
        } else {
            html += `<span class="current">${esc(step.name)}</span>`;
        }
    }
    bc.innerHTML = html;
}
async function browseUaSource(nodeId) {
    const source = currentSource();
    if (!source) return;
    const targetNodeId = nodeId || 'i=85';
    state.tagPath = targetNodeId;
    renderUaCrumb();
    el('tagTree').innerHTML = '<span class="msg">Browsing…</span>';
    el('tagStatus').textContent = 'Loading OPC UA nodes…';
    const body = {
        sourceId: source.sourceId,
        endpointUrl: source.endpointUrl || source.EndpointUrl,
        securityMode: source.securityMode || source.SecurityMode || 'None',
        securityPolicy: source.securityPolicy || source.SecurityPolicy || 'None',
        nodeId: targetNodeId,
        maxNodes: 200
    };
    const res = await fetch('/api/ua/browse', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const p = await res.json();
    if (p.error) throw new Error(p.error);
    const nodes = p.nodes || [];
    const mappedKeys = new Set((state.mappings || []).map(m => valueKey(m.sourceId || m.SourceId || 'default', m.itemId || m.ItemId || m.daItemId || m.DaItemId)));
    const rows = [];
    if (state.uaBrowseTrail.length) {
        // Trail stores the NODE WE DESCENDED INTO. Parent of the currently
        // displayed node is the previous trail entry (or '' for root).
        const parentTrail = state.uaBrowseTrail.slice(0, -1);
        const parentNodeId = parentTrail.length ? parentTrail[parentTrail.length - 1].nodeId : '';
        rows.push(`<div class="li clickable" data-action="open-branch" data-path="${attr(parentNodeId)}" data-trail-depth="${parentTrail.length}"><span class="icon folder">&#9650;</span><div style="flex:1"><div class="n">..</div><div class="p">Up one level</div></div></div>`);
    }
    let folders = 0, vars = 0;
    for (const node of nodes) {
        const nid = node.nodeId || '';
        const name = node.displayName || node.DisplayName || nid;
        const cls = String(node.nodeClass || node.NodeClass || '').toLowerCase();
        const hasChildren = node.hasChildren || node.HasChildren;
        if (cls === 'variable') {
            vars++;
            const isMapped = mappedKeys.has(valueKey(source.sourceId, nid));
            rows.push(`<div class="li"><span class="icon tag">&#9878;</span><div style="flex:1"><div class="n">${esc(name)}</div><div class="p">${esc(nid)} · Variable</div></div><div class="li-actions">${isMapped ? '<span class="mapped-badge">Mapped</span>' : ''}<button class="btn ghost" data-action="add-tag" data-source-id="${attr(source.sourceId)}" data-item-id="${attr(nid)}" data-name="${attr(name)}">Map</button></div></div>`);
        } else {
            folders++;
            const childIcon = hasChildren ? '&#128193;' : '&#128196;';
            rows.push(`<div class="li clickable" data-action="open-branch" data-path="${attr(nid)}" data-node-name="${attr(name)}"><span class="icon folder">${childIcon}</span><div style="flex:1"><div class="n">${esc(name)}</div><div class="p">${esc(nid)} · ${esc(node.nodeClass || 'folder')}${hasChildren ? '' : ' (leaf)'}</div></div></div>`);
        }
    }
    el('tagTree').innerHTML = rows.length ? rows.join('') : '<span class="msg">No child nodes at this node.</span>';
    el('tagStatus').textContent = folders + ' folders · ' + vars + ' variables';
}
async function browseTags(path, recursive = false) {
    const source = currentSource();
    if (!source || state.editingNewSource) {
        el('tagTree').innerHTML = '<span class="msg">Select or save a source before browsing tags.</span>';
        el('tagBreadcrumb').innerHTML = '';
        return;
    }
    if (!sourceMatchesMapType(source)) {
        // The active map-type tab (OPC DA / OPC UA / Drivers / MX) may only browse
        // sources of its own type. A stale selection from another type — e.g. an OPC DA
        // source left selected while the OPC UA tab has no sources — must never fall
        // through to the DA browse and show DA tags on the wrong tab.
        el('tagTree').innerHTML = `<span class="msg">This tab browses ${mapTypeLabel()} sources only — select one from the Source dropdown.</span>`;
        el('tagBreadcrumb').innerHTML = '';
        return;
    }
    if (isUaSource(source)) {
        await browseUaSource(path || '');
        return;
    }
    state.uaBrowseTrail = [];
    state.tagPath = path || '';
    renderCrumb();
    el('tagTree').innerHTML = '<span class="msg">Browsing…</span>';
    el('tagStatus').textContent = recursive ? 'Loading all tags…' : 'Loading folder…';
    const body = {
        sourceId: source.sourceId,
        progId: source.progId,
        host: source.host || 'localhost',
        path: state.tagPath,
        recursive,
        remoteUsername: source.remoteUsername || null,
        remotePassword: null,
        remoteDomain: source.remoteDomain || null
    };
    const p = await (await fetch('/api/da/tags', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })).json();
    if (p.error) throw new Error(p.error);
    const branches = p.branches || [];
    const tags = p.tags || [];
    const mappedKeys = new Set((state.mappings || []).map(m => valueKey(m.sourceId || m.SourceId || 'default', m.itemId || m.ItemId || m.daItemId || m.DaItemId)));
    const rows = [];
    if (state.tagPath) {
        const parent = state.tagPath.includes('.') ? state.tagPath.substring(0, state.tagPath.lastIndexOf('.')) : '';
        rows.push(`<div class="li clickable" data-action="open-branch" data-path="${attr(parent)}"><span class="icon folder">&#9650;</span><div style="flex:1"><div class="n">..</div><div class="p">Up one level</div></div></div>`);
    }
    for (const branch of branches) {
        const child = state.tagPath ? state.tagPath + '.' + branch : branch;
        rows.push(`<div class="li clickable" data-action="open-branch" data-path="${attr(child)}"><span class="icon folder">&#128193;</span><div style="flex:1"><div class="n">${esc(branch)}</div><div class="p">folder</div></div></div>`);
    }
    for (const tag of tags) {
        const itemId = tag.itemId || tag.ItemId || tag.daItemId || tag.DaItemId;
        const name = tag.name || tag.Name || itemId;
        const isMapped = mappedKeys.has(valueKey(source.sourceId, itemId));
        rows.push(`<div class="li"><span class="icon tag">&#9878;</span><div style="flex:1"><div class="n">${esc(name)}</div><div class="p">${esc(itemId)}</div></div><div class="li-actions">${isMapped ? '<span class="mapped-badge">Mapped</span>' : ''}<button class="btn ghost" data-action="add-tag" data-source-id="${attr(source.sourceId)}" data-item-id="${attr(itemId)}" data-name="${attr(name)}">Add</button></div></div>`);
    }
    el('tagTree').innerHTML = rows.length ? rows.join('') : '<span class="msg">No tags or folders here.</span>';
    el('tagStatus').textContent = branches.length + ' folders · ' + tags.length + ' tags';
}
async function addTag(sourceId, itemId, name) {
    await fetch('/api/mappings/add', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ tags: [{ sourceId, itemId: itemId, displayName: name || itemId, dataType: 'Auto', uaNodeId: defaultUaNodeId(sourceId, itemId) }] })
    });
    await loadMappings();
    await refresh();
}
async function addManual() {
    const itemId = el('manualItem').value.trim();
    const source = currentSource();
    if (!itemId || !source || state.editingNewSource) return;
    const sourceId = source.sourceId;
    const uaNodeId = el('manualUaNodeId').value.trim() || defaultUaNodeId(sourceId, itemId);
    await fetch('/api/mappings/add', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ tags: [{ sourceId, itemId: itemId, displayName: itemId, dataType: 'Auto', uaNodeId }] })
    });
    el('manualItem').value = '';
    el('manualUaNodeId').value = '';
    await loadMappings();
    await refresh();
}
async function removeMapping(sourceId, itemId) {
    await fetch('/api/mappings/remove', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sourceId, itemId: itemId })
    });
    await loadMappings();
    await refresh();
}
function toggleLiveValues() {
    state.liveValuesEnabled = !state.liveValuesEnabled;
    updateLiveValuesUi();
    if (state.liveValuesEnabled) {
        refresh().catch(e => {
            el('dot').className = 'dot off';
            el('clock').textContent = 'offline';
            el('values').innerHTML = `<tr><td colspan="7" class="bad">${esc(e.message)}</td></tr>`;
        });
    }
}

function bindDynamicButtons() {
    el('sourcesList').addEventListener('click', event => {
        const button = event.target.closest('button[data-action="select-source"]');
        if (!button) return;
        pickSource(button.dataset.sourceId || '');
    });
    const statusList = el('sourcesStatusList');
    if (statusList) {
        statusList.addEventListener('click', event => {
            const button = event.target.closest('button[data-action="select-source-status"]');
            if (!button) return;
            pickSource(button.dataset.sourceId || '', { openConfig: true });
        });
    }
    const uaList = el('uaSourcesList');
    if (uaList) {
        uaList.addEventListener('click', event => {
            const button = event.target.closest('button[data-action="select-ua-source"]');
            if (!button) return;
            pickSource(button.dataset.sourceId || '');
        });
    }
    el('listServers').addEventListener('click', event => {
        const button = event.target.closest('button[data-action="pick-server"]');
        if (!button) return;
        pickServer(button.dataset.progId || '', button.dataset.host || 'localhost');
    });
    el('listUaDiscover').addEventListener('click', event => {
        const button = event.target.closest('button[data-action="pick-ua-server"]');
        if (!button) return;
        pickUaServer(button.dataset.url || '', button.dataset.name || '');
    });
    el('wzListServers').addEventListener('click', event => {
        const button = event.target.closest('button[data-action="wz-pick-server"]');
        if (!button) return;
        wzPickServer(button.dataset.progId || '', button.dataset.host || 'localhost');
    });
    el('tagTree').addEventListener('click', event => {
        const actionEl = event.target.closest('[data-action]');
        if (!actionEl) return;
        if (actionEl.dataset.action === 'open-branch') {
            if (isUaSource(currentSource())) {
                const depth = parseInt(actionEl.dataset.trailDepth || '', 10);
                if (!isNaN(depth)) {
                    state.uaBrowseTrail = state.uaBrowseTrail.slice(0, depth);
                } else {
                    // Descend: trail entry stores the CHILD nodeId so the crumb link
                    // navigates to the child and '..' derives the parent as the previous entry.
                    const childNodeId = actionEl.dataset.path || '';
                    const childName = actionEl.dataset.nodeName || childNodeId;
                    state.uaBrowseTrail.push({ nodeId: childNodeId, name: childName });
                }
            }
            browseTags(actionEl.dataset.path || '').catch(e => el('tagTree').innerHTML = `<span class="bad">${esc(e.message)}</span>`);
            return;
        }
        if (actionEl.tagName === 'BUTTON' && actionEl.dataset.action === 'add-tag') {
            addTag(actionEl.dataset.sourceId || '', actionEl.dataset.itemId || '', actionEl.dataset.name || '').catch(e => alert('Add failed: ' + e.message));
        }
    });
    el('tagBreadcrumb').addEventListener('click', event => {
        const link = event.target.closest('a[data-crumb]');
        if (!link) return;
        if (isUaSource(currentSource())) {
            const depth = parseInt(link.dataset.crumbDepth || '0', 10) || 0;
            state.uaBrowseTrail = state.uaBrowseTrail.slice(0, depth);
        }
        browseTags(link.dataset.crumb || '').catch(e => el('tagTree').innerHTML = `<span class="bad">${esc(e.message)}</span>`);
    });
    el('interlinkConsumerList').addEventListener('click', event => {
        const btn = event.target.closest('button[data-action="pick-interlink-consumer"]');
        if (!btn) return;
        setInterlinkSelection('consumer', btn.dataset.sourceId || '', btn.dataset.itemId || '', btn.dataset.name || '');
    });
    el('interlinkProviderList').addEventListener('click', event => {
        const btn = event.target.closest('button[data-action="pick-interlink-provider"]');
        if (!btn) return;
        setInterlinkSelection('provider', btn.dataset.sourceId || '', btn.dataset.itemId || '', btn.dataset.name || '');
    });
    el('mappedList').addEventListener('click', event => {
        const row = event.target.closest('[data-action="open-faceplate"]');
        if (!row) return;
        openFaceplate(row.dataset.sourceId || '', row.dataset.itemId || '');
    });
    el('faceplateOverlay').addEventListener('click', event => {
        const button = event.target.closest('button[data-action]');
        if (!button) return;
        const sourceId = button.dataset.sourceId || '';
        const itemId = button.dataset.itemId || '';
        if (button.dataset.action === 'remove-mapping') {
            removeMapping(sourceId, itemId).then(() => closeFaceplate()).catch(e => alert('Remove failed: ' + e.message));
            return;
        }
        if (button.dataset.action === 'save-tag') {
            updateMapping(sourceId, itemId, payload => {
                const simulated = el('fpSimulated').checked;
                payload.displayName = el('fpDisplayName').value.trim() || itemId;
                payload.accessRights = el('fpAccess').value;
                const rateValue = el('fpPollRate').value;
                if (rateValue.startsWith('@')) {
                    // legacy fixed rate kept until the user picks something else
                    payload.daGroup = null;
                    payload.pollRateMs = Number.parseInt(rateValue.slice(1), 10) || 0;
                } else if (!rateValue) {
                    payload.daGroup = null;          // Source Default
                    payload.pollRateMs = 0;
                } else if (/^\d+$/.test(rateValue)) {
                    payload.daGroup = null;          // plain numeric legacy choice
                    payload.pollRateMs = Number.parseInt(rateValue, 10);
                } else {
                    payload.daGroup = rateValue;     // named DA group
                    const srcId = el('fpApply').dataset.sourceId || sourceId;
                    payload.pollRateMs = daGroupRateFor(srcId, rateValue) || 1000;
                }
                if (el('fpSubscriptionField') && el('fpSubscriptionField').style.display !== 'none' && el('fpSubscription')) {
                    payload.subscription = el('fpSubscription').value.trim(); // '' = source default
                }
                payload.deadbandPct = Math.max(0, Math.min(100, Number.parseFloat(el('fpDeadband').value) || 0));
                payload.description = el('fpDescription').value.trim() || null;
                if (simulated) {
                    payload.mode = 'Manual';
                    const manualField = el('fpManualInput');
                    if (!manualField.value.trim()) {
                        const liveText = el('fpLivePanel')?.querySelector('.fp-v')?.textContent || '';
                        manualField.value = liveText;
                    }
                    payload.manualValue = manualField.value.trim() || '';
                } else {
                    payload.mode = 'Source';
                    payload.manualValue = null;
                }
            }).then(() => {
                el('mappingMessage').textContent = 'Mapping updated.';
                openFaceplate(sourceId, itemId);
            }).catch(e => alert('Update failed: ' + e.message));
        }
    });
    el('faceplateOverlay').addEventListener('change', event => {
        const target = event.target;
        if (!(target instanceof HTMLInputElement || target instanceof HTMLSelectElement)) return;
        if (target.id === 'fpEnabled') {
            updateMapping(target.dataset.sourceId || '', target.dataset.itemId || '', payload => {
                payload.enabled = target.checked;
                if (!target.checked) { payload.mode = 'Source'; payload.manualValue = null; payload.writeable = false; }
            }).then(() => openFaceplate(target.dataset.sourceId || '', target.dataset.itemId || '')).catch(e => alert('Update failed: ' + e.message));
            return;
        }
        if (target.id === 'fpSimulated') {
            updateManualInputState();
        }
        if (target.id === 'fpAccess') {
            updateManualInputState();
        }
        if (target.id === 'fpSubscription') {
            updateFpRateEnabled();
        }
    });
}



document.addEventListener('DOMContentLoaded', async () => {
    bindDiagramPanZoom();
    el('selectedSource').addEventListener('change', e => pickSource(e.target.value));
    el('mapSourceSelect').addEventListener('change', e => pickSource(e.target.value));
    if (el('uaSelectedSource')) el('uaSelectedSource').addEventListener('change', e => pickSource(e.target.value));
    el('cfgApply').addEventListener('click', () => saveSource().catch(e => el('cfgMessage').textContent = '✗ ' + e.message));
    el('cfgReset').addEventListener('click', resetSource);
    el('cfgNew').addEventListener('click', newSource);
    el('cfgRemove').addEventListener('click', () => removeSelectedSource().catch(e => el('cfgMessage').textContent = '✗ ' + e.message));
    el('drvA3nSave').addEventListener('click', () => saveDriverSource().catch(e => el('drvA3nMessage').textContent = '✗ ' + e.message));
    el('drvA3nReset').addEventListener('click', resetDriver);
    el('drvA3nNew').addEventListener('click', newDriver);
    el('drvA3nRemove').addEventListener('click', () => removeDriver().catch(e => el('drvA3nMessage').textContent = '✗ ' + e.message));
    el('drvA3nTest').addEventListener('click', () => testDriverConnection().catch(e => el('drvA3nMessage').textContent = '✗ ' + e.message));
    if (el('mxSave')) el('mxSave').addEventListener('click', () => saveMxSource().catch(e => el('mxMessage').textContent = '✗ ' + e.message));
    if (el('mxReset')) el('mxReset').addEventListener('click', resetMx);
    if (el('mxNew')) el('mxNew').addEventListener('click', newMxSource);
    if (el('mxRemove')) el('mxRemove').addEventListener('click', () => removeMxSource().catch(e => el('mxMessage').textContent = '✗ ' + e.message));
    if (el('mxTest')) el('mxTest').addEventListener('click', () => testMxConnection().catch(e => el('mxMessage').textContent = '✗ ' + e.message));
    if (el('mxList')) {
        el('mxList').addEventListener('click', event => {
            const button = event.target.closest('button[data-action="select-mx"]');
            if (!button) return;
            pickMxSource(button.dataset.sourceId || '');
        });
    }
    if (el('btnDrvScanPorts')) el('btnDrvScanPorts').addEventListener('click', () => scanSerialPorts('drvA3nPort', 'listDrvPorts', 'msgDrvPorts').catch(e => el('msgDrvPorts').textContent = '✗ ' + e.message));
    if (el('btnWzDrvScanPorts')) el('btnWzDrvScanPorts').addEventListener('click', () => scanSerialPorts('wzDrvPort', 'listWzDrvPorts', 'msgWzDrvPorts').catch(e => el('msgWzDrvPorts').textContent = '✗ ' + e.message));
    const onUseSerialPort = event => {
        const button = event.target.closest('button[data-action="use-serial-port"]');
        if (!button) return;
        useSerialPort(button.dataset.port || '', button.dataset.target || '');
    };
    if (el('listDrvPorts')) el('listDrvPorts').addEventListener('click', onUseSerialPort);
    if (el('listWzDrvPorts')) el('listWzDrvPorts').addEventListener('click', onUseSerialPort);
    el('drvA3nList').addEventListener('click', event => {
        const button = event.target.closest('button[data-action="select-driver"]');
        if (!button) return;
        pickDriver(button.dataset.sourceId || '');
    });
    ['cfgSourceId','cfgDisplayName','cfgProgId','cfgHost','cfgUser','cfgPass','cfgDomain'].forEach(id => {
        el(id).addEventListener('input', () => { if (!state.editingNewSource) showSaveReset(); });
    });
    el('uaCfgApply').addEventListener('click', () => saveUaSource().catch(e => el('uaCfgMessage').textContent = '✗ ' + e.message));
    el('uaCfgReset').addEventListener('click', resetUaSource);
    el('uaCfgNew').addEventListener('click', newUaSource);
    el('uaCfgRemove').addEventListener('click', () => removeSelectedUaSource().catch(e => el('uaCfgMessage').textContent = '✗ ' + e.message));
    el('btnUaTestConnection').addEventListener('click', () => testUaConnection().catch(e => el('uaCfgMessage').textContent = '✗ ' + e.message));
    el('btnUaDiscover').addEventListener('click', () => discoverUaServers().catch(e => el('msgUaDiscover').textContent = e.message));
    ['uaCfgSourceId','uaCfgDisplayName','uaCfgEndpointUrl','uaCfgSecurityMode','uaCfgSecurityPolicy','uaCfgUser','uaCfgPass','uaCfgUpdateRate','uaCfgMaxMappedTags','uaCfgUseSubscriptions'].forEach(id => {
        const node = el(id);
        if (!node) return;
        const evt = node.tagName === 'SELECT' || node.type === 'checkbox' ? 'change' : 'input';
        node.addEventListener(evt, () => { if (!state.editingNewUaSource) showUaSaveReset(); });
    });
    el('cfgApplyRate').addEventListener('click', () => saveUpdateRate().catch(e => el('rateMessage').textContent = '✗ ' + e.message));
    el('btnExportConfig').addEventListener('click', async () => {
        try {
            const r = await fetch('/api/config/export');
            const blob = await r.blob();
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = `opcbridge-config-${new Date().toISOString().slice(0,10)}.json`;
            a.click();
            URL.revokeObjectURL(url);
            el('configMessage').textContent = 'Config exported.';
        } catch (e) { el('configMessage').textContent = '✗ ' + e.message; }
    });
    el('btnImportConfig').addEventListener('click', () => el('importConfigFile').click());
    el('importConfigFile').addEventListener('change', async e => {
        const file = e.target.files[0];
        if (!file) return;
        try {
            const text = await file.text();
            const r = await fetch('/api/config/import', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: text });
            const p = await r.json();
            if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
            el('configMessage').textContent = 'Config imported. Re-enter DCOM passwords and save each source.';
            await loadSources();
            await loadMappings();
            await refresh();
        } catch (err) { el('configMessage').textContent = '✗ ' + err.message; }
        e.target.value = '';
    });
    const ioModeSel = el('cfgIoMode');
    if (ioModeSel) ioModeSel.addEventListener('change', async () => {
        const ioModeHint = el('ioModeHint');
        if (state.editingNewSource) {
            if (ioModeHint) ioModeHint.textContent = 'Will apply when the source is saved.';
            showSaveReset();
            return;
        }
        const src = currentSource();
        if (!src) return;
        try {
            const r = await fetch('/api/da/sources/io-mode', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ sourceId: src.sourceId, ioMode: ioModeSel.value })
            });
            const p = await r.json();
            if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
            if (ioModeHint) ioModeHint.textContent = 'Applied live — requested: ' + p.ioMode + ' · effective: see Read Mode above';
            await loadSources();
            await refresh();
        } catch (err) {
            if (ioModeHint) ioModeHint.textContent = '✗ ' + err.message;
        }
    });
    el('cfgUseSubscriptions').addEventListener('change', async e => {
        try {
            const r = await fetch('/api/da/use-subscriptions', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ useSubscriptions: e.target.checked })
            });
            const p = await r.json();
            if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
            state.useSubscriptions = p.useSubscriptions;
            el('cfgUseSubscriptions').checked = state.useSubscriptions;
            el('subMessage').textContent = state.useSubscriptions ? 'ON — applies on next reconnect' : 'OFF — polling mode, applies on next reconnect';
            await refresh();
        } catch (err) {
            el('subMessage').textContent = '✗ ' + err.message;
            el('cfgUseSubscriptions').checked = state.useSubscriptions;
        }
    });
    el('btnReloadServers').addEventListener('click', () => browseServers().catch(e => el('msgServers').textContent = e.message));
    el('btnBrowseTags').addEventListener('click', () => browseTags('').catch(e => el('tagTree').innerHTML = `<span class="bad">${esc(e.message)}</span>`));
    el('btnBrowseAllTags').addEventListener('click', () => browseTags('', true).catch(e => el('tagTree').innerHTML = `<span class="bad">${esc(e.message)}</span>`));
    el('manualAdd').addEventListener('click', () => addManual().catch(e => alert('Add failed: ' + e.message)));
    el('mappingFilter').addEventListener('input', e => { state.mappingFilter = e.target.value; rerenderMappings(); });
    el('mappingSort').addEventListener('change', e => { state.mappingSort = e.target.value; rerenderMappings(); });
    el('mappingSortDir').addEventListener('click', () => { state.mappingSortDir *= -1; el('mappingSortDir').textContent = state.mappingSortDir > 0 ? '↑' : '↓'; rerenderMappings(); });
    el('toggleLiveValues').addEventListener('click', toggleLiveValues);
    const lvSourceSelect = el('liveValuesSource');
    if (lvSourceSelect) lvSourceSelect.addEventListener('change', e => {
        state.liveValuesSource = e.target.value || '';
        updateLiveValuesUi();
        refresh().catch(err => {
            el('dot').className = 'dot off';
            el('clock').textContent = 'offline';
        });
    });
    el('btnRefreshLogs').addEventListener('click', () => loadLogs(true).catch(e => el('logMessage').textContent = '✗ ' + e.message));
    el('logLevel').addEventListener('change', () => {
        state.logsLoaded = false;
        loadLogs(true).catch(e => el('logMessage').textContent = '✗ ' + e.message);
    });
    el('logLimit').addEventListener('change', () => {
        state.logsLoaded = false;
        loadLogs(true).catch(e => el('logMessage').textContent = '✗ ' + e.message);
    });
    el('btnSetLink').addEventListener('click', () => saveInterlink(state.interlinkDraft.consumer ? state.interlinkDraft.consumer.key : '', state.interlinkDraft.provider ? state.interlinkDraft.provider.key : '').catch(e => el('linksMessage').textContent = '✗ ' + e.message));
    el('btnClearLink').addEventListener('click', () => {
        const consumerKey = state.interlinkDraft.consumer ? state.interlinkDraft.consumer.key : '';
        const existing = findInterlinkByConsumer(consumerKey);
        deleteInterlink(existing ? (existing.id || existing.Id || '') : '').catch(e => el('linksMessage').textContent = '✗ ' + e.message);
    });
    el('btnClearLinkSelection').addEventListener('click', () => clearInterlinkDraftSelection());
    el('linksList').addEventListener('click', event => {
        const btn = event.target.closest('button[data-action="unlink"]');
        if (!btn) return;
        deleteInterlink(btn.dataset.linkId || '').catch(e => el('linksMessage').textContent = '✗ ' + e.message);
    });
    bindDynamicButtons();
    const LEGACY_TAB_TO_ROUTE = {
      monitor: 'ops/monitor',
      connection: 'connectivity/sources',
      'opc-da': 'connectivity/opc-da',
      'opc-ua': 'connectivity/opc-ua',
      diagnostics: 'ops/diagnostics',
      sessions: 'ops/sessions',
      tags: 'tags/maps',
      links: 'tags/interlinks',
      logs: 'ops/logs',
      mqtt: 'iot/mqtt',
      'iot-traffic': 'iot/traffic',
      influx: 'historian/influx',
      diagram: 'ops/diagram',
      help: 'help/guide',
      about: 'help/about'
    };
    const initHashRaw = location.hash.replace(/^#\/?/, '');
    let initRoute = Object.prototype.hasOwnProperty.call(ROUTE_TO_TAB, initHashRaw) ? initHashRaw
      : (LEGACY_TAB_TO_ROUTE[initHashRaw] || DEFAULT_ROUTE);
    await navigate(initRoute);
    await loadSources();
    await loadMappings();
    if (document.getElementById('view-tags')?.classList.contains('active')) {
      syncMapTypeUi();
      ensureMapSourceSelection();
      renderMapSourceSelect();
      updateMapEmptyBanner();
      updateMapBrowseUi();
      rerenderMappings();
    }
    updateLiveValuesUi();
    setInterval(refresh, 1000);
    setInterval(() => { if (el('logAutoRefresh')?.checked && document.querySelector('#view-logs.active')) { state.logsLoaded = false; loadLogs(true).catch(() => {}); } }, 3000);
    setInterval(() => { if (diagnosticsActive) loadDiagnostics().catch(() => {}); }, 2000);
    setInterval(() => {
        if (document.querySelector('#view-mqtt.active')) {
            loadMqttStatus().catch(() => {});
        }
        if (document.querySelector('#view-iot-traffic.active')) {
            if (el('mqttValAuto')?.checked) loadMqttValues().catch(() => {});
        }
        if (document.querySelector('#view-influx.active')) loadInfluxStatus().catch(() => {});
    }, 2000);
    if (initRoute === 'logs') await loadLogs();
    if (initRoute === 'help') await loadHelp();
    if (initRoute === 'about') await loadAppInfo();
    fetch('/api/version').then(r => r.json()).then(p => { const v = (p.informationalVersion || p.version || '0.0.0').split('+')[0]; el('appVersion').textContent = 'v' + v; }).catch(() => {});
});
</script>
</body>
</html>
""";

    public static string FullHtml => Html + Script;
}
