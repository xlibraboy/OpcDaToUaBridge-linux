namespace OpcBridge.App;

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
.tabbar { display: flex; flex-direction: column; background: var(--panel); border-right: 1px solid var(--border2); padding: 8px 0; width: 152px; flex-shrink: 0; overflow-y: auto; }
.tabbtn { background: none; border: none; color: var(--muted); padding: 11px 16px; font-size: 13px; font-weight: 500; cursor: pointer; border-left: 3px solid transparent; display: flex; align-items: center; gap: 8px; text-align: left; }
.tabbtn:hover { color: var(--text); background: var(--panel2); }
.tabbtn.active { color: var(--accent); border-left-color: var(--accent); background: var(--panel2); }
.content { flex: 1; min-width: 0; overflow: auto; }
.view { display: none; padding: 16px 18px; }
.view.active { display: block; }
@media (max-width: 600px) { .app-shell { flex-direction: column; } .tabbar { flex-direction: row; width: 100%; border-right: none; border-bottom: 1px solid var(--border2); padding: 0 8px; overflow-x: auto; } .tabbtn { border-left: none; border-bottom: 3px solid transparent; padding: 10px 14px; } .tabbtn.active { border-left: none; border-bottom-color: var(--accent); } }
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
        .li .li-badge { margin-left: auto; }
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
        .rate-limit-table th { text-align: left; padding: 5px 8px; border-bottom: 1px solid var(--border2); color: var(--muted); font-size: 10px; text-transform: uppercase; letter-spacing: .05em; }
        .rate-limit-table td { padding: 5px 8px; border-bottom: 1px solid var(--border); }
        .rate-limit-table td:first-child { font-weight: 600; white-space: nowrap; }
        .rate-limit-table td:nth-child(2) { text-align: center; white-space: nowrap; }

        .log-entry .message { white-space: pre-wrap; word-break: break-word; }
        .log-entry .exception { white-space: pre-wrap; word-break: break-word; margin-top: 6px; color: var(--bad); }
        .log-entry .meta .lvl { font-weight: 600; }
        .log-entry .meta .lvl.trace, .log-entry .meta .lvl.debug { color: var(--muted); }
        .log-entry .meta .lvl.information { color: var(--accent); }
        .log-entry .meta .lvl.warning { color: var(--warn); }
        .log-entry .meta .lvl.error, .log-entry .meta .lvl.critical { color: var(--bad); }
        .log-entry .message.error, .log-entry .message.critical { color: var(--bad); }
        .help-subtabs { display: flex; gap: 2px; background: var(--panel); border: 1px solid var(--border); border-radius: 6px; padding: 4px; margin-bottom: 12px; }
        .help-subtab { flex: 1; background: none; border: none; color: var(--muted); padding: 8px 16px; font-size: 12px; font-weight: 600; cursor: pointer; border-radius: 4px; transition: all .15s ease; }
        .help-subtab:hover { color: var(--text); background: var(--panel2); }
        .help-subtab.active { color: var(--text); background: var(--panel2); box-shadow: 0 1px 3px rgba(0,0,0,.2); }
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
        .help-body ul { padding-left: 18px; color: var(--muted); }
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
        .help-body td:nth-child(2) { text-align: center; white-space: nowrap; }
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
        .diag-tab {
            background: var(--panel2);
            border: 1px solid var(--border);
            color: var(--text);
            padding: 6px 14px;
            border-radius: 4px;
            cursor: pointer;
            font-size: 12px;
            font-weight: 500;
            transition: all 0.15s;
        }
        .diag-tab:hover {
            background: var(--border);
        }
        .diag-tab.active {
            background: var(--accent);
            border-color: var(--accent);
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
        .legend-item {
            display: flex;
            align-items: center;
            gap: 6px;
        }
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
            transition: stroke 0.3s;
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
    </style>
</head>
<body>
<div class="topbar">
    <div class="brand"><span class="dot" id="dot"></span>OPC DA&#8594;UA Bridge <span class="ver" id="appVersion"></span></div>
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
    <button class="tabbtn active" data-tab="monitor" onclick="showTab('monitor')">Monitor</button>
    <button class="tabbtn" data-tab="connection" onclick="showTab('connection')">Connection</button>
    <button class="tabbtn" data-tab="diagnostics" onclick="showTab('diagnostics')">Diagnostics</button>
    <button class="tabbtn" data-tab="tags" onclick="showTab('tags')">Tags</button>
    <button class="tabbtn" data-tab="links" onclick="showTab('links')">OPC DA to DA</button>
    <button class="tabbtn" data-tab="logs" onclick="showTab('logs')">Logs</button>
    <button class="tabbtn" data-tab="mqtt" onclick="showTab('mqtt')">MQTT</button>
    <button class="tabbtn" data-tab="influx" onclick="showTab('influx')">InfluxDB</button>
    <button class="tabbtn" data-tab="diagram" onclick="showTab('diagram')">Diagram</button>
    <button class="tabbtn" data-tab="help" onclick="showTab('help')">Help</button>
    <button class="tabbtn" data-tab="about" onclick="showTab('about')">About</button>
</div>
<div class="content">
<div class="view active" id="view-monitor">
    <div class="alarm-bar" id="rateAlarmBar" style="display:none"></div>
    <div class="mon-stats">
        <div class="mon-stat-group">
            <div class="mon-stat-group-h">Bridge</div>
            <div class="stat"><div class="k">Runtime</div><div class="v" id="bridgeState">&#8212;</div><div class="s" id="lastError">No errors</div></div>
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
    <div class="box">
        <div class="box-h">DA Live Values <span class="msg" id="valCount" style="margin-left:auto"></span><button class="btn ghost" id="toggleLiveValues" type="button">Disable Live Data</button></div>
        <div class="box-b" style="padding:0">
            <div class="values-wrap">
                <table class="values-table">
                    <colgroup><col style="width:14%"><col style="width:29%"><col style="width:27%"><col style="width:14%"><col style="width:16%"></colgroup>
                    <thead><tr><th>Source</th><th>DA Item ID</th><th>Value</th><th>Quality</th><th>Timestamp</th></tr></thead>
                    <tbody id="values"><tr><td colspan="5" class="msg">Waiting for values&#8230;</td></tr></tbody>
                </table>
            </div>
        </div>
    </div>
</div>
<div class="view" id="view-diagnostics">
    <div class="box" style="margin-bottom:14px">
        <div class="box-h">DA Source Diagnostics <span class="msg" id="diagDaSummary" style="margin-left:auto"></span></div>
        <div class="box-b" id="diagDaSources"><span class="msg">Loading…</span></div>
    </div>
    <div class="box" style="margin-bottom:14px">
        <div class="box-h">Time Sync <span class="info" data-tip="Compares the DA server's clock to the bridge machine's clock. A large offset (>500ms) indicates the DA server or bridge needs NTP time sync. UA clients receive both SourceTimestamp (DA server time) and ServerTimestamp (bridge time) for each value.">i</span></div>
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
    <div class="grid2" style="margin-bottom:14px">
        <div class="box">
            <div class="box-h">UA Bandwidth <span class="info" data-tip="Notifications/sec counts how many value changes were pushed to UA nodes. Estimated bandwidth = notifications/sec x ~80 bytes (typical UA notification encoding). The SDK does not expose actual wire bytes.">i</span></div>
            <div class="box-b">
                <div class="stats">
                    <div class="stat"><div class="k">Notifications/sec</div><div class="v" id="diagNotifPerSec">&#8212;</div></div>
                    <div class="stat"><div class="k">Est. Bandwidth</div><div class="v" id="diagBandwidth">&#8212;</div><div class="s" id="diagTotalNotif">0 total</div></div>
                </div>
            </div>
        </div>
        <div class="box">
            <div class="box-h">Write Queue <span class="info" data-tip="UA client writes are queued in a bounded channel (capacity 1024) and drained by per-source consumer tasks. Success rate shows confirmed DA writes.">i</span></div>
            <div class="box-b">
                <div class="stats">
                    <div class="stat"><div class="k">Current Depth</div><div class="v" id="diagWqDepth">&#8212;</div></div>
                    <div class="stat"><div class="k">Success Rate</div><div class="v" id="diagWqRate">&#8212;</div><div class="s" id="diagWqTotals">0 enqueued</div></div>
                </div>
            </div>
        </div>
    </div>
    <div class="box">
        <div class="box-h">STA Thread Health <span class="info" data-tip="Each OPC DA source has a dedicated Single-Threaded Apartment (STA) thread. All COM calls for that source serialize through it. 'Queued' shows pending COM operations; 'Last action' shows the most recent COM call time.">i</span></div>
        <div class="box-b"><div class="list" id="diagStaThreads" style="max-height:280px"><span class="msg">Loading…</span></div></div>
    </div>
</div>
<div class="view" id="view-connection">
    <div class="conn-layout">
        <div class="conn-main">
            <div class="box">
                <div class="box-h">Server Connection <span class="msg" id="cfgMessage" style="margin-left:auto;font-weight:400;text-transform:none;letter-spacing:0">Select a saved connection or click New.</span></div>
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
                        <div class="conn-section-h">Credentials <span class="info" data-tip="Only required for remote DCOM with specific user accounts, or to access OPC DA servers registered in another user's profile.">i</span></div>
                        <div class="field"><label class="fl">User</label><input id="cfgUser" type="text" placeholder="username" style="flex:1"><input id="cfgPass" type="password" placeholder="password" style="flex:1"><input id="cfgDomain" type="text" placeholder="domain" style="flex:1"></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">Default Update Rate <span class="info" data-tip="Fallback update rate for tags set to 'Source Default' (Tags tab → faceplate → Update Rate). Tags with a specific rate override this.">i</span></div>
                        <div class="field"><label class="fl">Rate</label><select id="cfgUpdateRate"><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000">1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select><button class="btn ghost" id="cfgApplyRate" type="button">Apply</button><span class="msg" id="rateMessage">Applies live</span></div>
                    </div>
                    <div class="conn-section">
                        <div class="conn-section-h">DA Subscriptions <span class="info" data-tip="When ON, the bridge uses IOPCDataCallback to receive value changes from the DA server (faster, supports deadband). When OFF, the bridge polls with IOPCSyncIO.Read. Changing this requires a source reconnect.">i</span></div>
                        <div class="field"><label class="fl">Subscriptions</label><input type="checkbox" id="cfgUseSubscriptions" checked><span class="msg" id="subMessage">Applies on reconnect</span></div>
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
<div class="view" id="view-tags">
    <div class="box" style="margin-bottom:14px">
        <div class="box-h">Tag Browser</div>
        <div class="box-b">
            <div class="field" style="margin-bottom:10px">
                <label class="fl">DA Source</label>
                <select id="mapSourceSelect"></select>
                <span class="msg" id="tagSourceStatus"></span>
            </div>
            <div class="tag-browser-toolbar">
                <button class="btn" id="btnBrowseAllTags" type="button">Browse All Tags</button>
                <button class="btn ghost" id="btnBrowseTags" type="button">Browse Folders</button>
                <span class="msg" id="tagStatus">Browse all tags, or open folders one level at a time.</span>
            </div>
            <div class="breadcrumb" id="tagBreadcrumb"></div>
            <div class="list" id="tagTree"></div>
        </div>
    </div>
    <div class="box">
        <div class="box-h">DA → OPC UA Mappings <span class="msg" id="mapCount" style="margin-left:auto"></span></div>
        <div class="box-b">
            <div class="add-mapping-box">
                <div class="field">
                    <input id="manualItem" type="text" placeholder="DA Item ID (e.g. Random.Real8)" style="flex:1">
                    <input id="manualUaNodeId" type="text" placeholder="UA NodeId (optional)" style="flex:1">
                </div>
                <div class="field" style="margin-bottom:0">
                    <button class="btn" type="button" id="manualAdd">Add Mapping</button>
                    <span class="msg">Or browse tags above and click Add.</span>
                </div>
            </div>
            <div class="hint" id="mappingMessage" style="margin-bottom:10px">Click a tag to open its faceplate. Disable a tag to stop publishing it, or set a manual value to override the DA source.</div>
            <div class="mapping-toolbar">
                <input id="mappingFilter" type="text" placeholder="Filter by name, item ID, UA node, source…" style="flex:1;min-width:120px">
                <label class="fl" style="width:auto">Sort</label>
                <select id="mappingSort">
                    <option value="name">Name</option>
                    <option value="source">Server (Source)</option>
                    <option value="item">DA Item ID</option>
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
<div class="view" id="view-links">
    <div class="box">
        <div class="box-h">DA Links <span class="msg" id="linksCount" style="margin-left:auto"></span></div>
        <div class="box-b">
            <div class="hint" id="linksMessage" style="margin-bottom:10px">Create provider-consumer DA rules here. DA Links are separate from OPC UA tag mappings.</div>
            <div class="field" style="margin-bottom:10px">
                <label class="fl">Active Source</label>
                <span class="msg" id="linkSourceStatus">Select a saved source, then browse tags below.</span>
            </div>
            <div class="fp-body" style="margin-bottom:10px">
                <div class="fp-panel">
                    <div class="fp-k">Consumer</div>
                    <div class="fp-meta" id="linkConsumerTarget"><span class="msg">Browse the active source and choose a consumer tag.</span></div>
                </div>
                <div class="fp-panel">
                    <div class="fp-k">Provider</div>
                    <div class="fp-meta" id="linkProviderTarget"><span class="msg">Browse the active source and choose a provider tag.</span></div>
                </div>
            </div>
            <div class="tag-browser-toolbar">
                <button class="btn" id="btnBrowseAllLinkTags" type="button">Browse All Tags</button>
                <button class="btn ghost" id="btnBrowseLinkTags" type="button">Browse Folders</button>
                <button class="btn" type="button" id="btnSetLink">Save Link</button>
                <button class="btn ghost" type="button" id="btnClearLink">Delete Saved Link</button>
                <button class="btn ghost" type="button" id="btnClearLinkSelection">Clear Selection</button>
                <span class="msg" id="linkBrowseStatus">Use the active source selection from Connection or Tags, then pick consumer/provider tags.</span>
            </div>
            <div class="breadcrumb" id="linkBrowseBreadcrumb"><span class="current">root</span></div>
            <div class="list" id="linkBrowseTree"><span class="msg">Use the active source to browse tags for DA links.</span></div>
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
            </div>
            <div class="fp-tabpane" id="fp-pane-basic">
                <div class="field"><label class="fl">Tag Name</label><input type="text" id="fpDisplayName" style="flex:1"></div>
                <div class="field"><label class="fl">DA Address</label><input type="text" id="fpDaItemId" readonly style="flex:1;opacity:.72"></div>
                <div class="field"><label class="fl">UA Node</label><input type="text" id="fpUaNodeId" readonly style="flex:1;opacity:.72"></div>
                <div class="field"><label class="fl">Description</label><input type="text" id="fpDescription" placeholder="Operator notes / tag description (optional)" style="flex:1"></div>
            </div>
            <div class="fp-tabpane" id="fp-pane-setup" style="display:none">
                <div class="field"><label class="fl">Access Rights</label><select id="fpAccess" data-action="tag-access"><option value="Read">Read (DA → UA)</option><option value="Read-Write">Read-Write (DA ↔ UA)</option><option value="Write">Write (UA → DA)</option></select></div>
                <div class="field"><label class="fl">Enabled</label><input type="checkbox" id="fpEnabled" data-action="toggle-tag-enabled"></div>
                <div class="field"><label class="fl">Update Rate</label><select id="fpPollRate" data-action="tag-poll-rate"><option value="0">Source Default</option><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000">1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select></div>
                <div class="field"><label class="fl">Deadband %</label><input type="number" id="fpDeadband" min="0" max="100" step="0.1" value="0" style="width:80px"></div>
                <div class="hint" style="margin-top:4px">Update Rate = DA group interval. With subscriptions on, the DA server pushes changes at this rate. With subscriptions off, the bridge polls at this rate.</div>
            </div>
            <div class="fp-tabpane" id="fp-pane-sim" style="display:none">
                <div class="field"><label class="fl">Simulated</label><input type="checkbox" id="fpSimulated" data-action="tag-simulated"></div>
                <div class="field"><label class="fl">Manual Value</label><input type="text" id="fpManualInput" data-action="tag-manual-value" disabled style="flex:1"></div>
                <div class="hint" id="fpModeHint" style="margin-top:4px"></div>
            </div>
            <div class="fp-tabpane" id="fp-pane-mqtt" style="display:none">
                <div class="field"><label class="fl">MQTT</label><input type="checkbox" id="fpMqttEnabled"> <span class="msg">publish/subscribe this tag</span></div>
                <div class="field"><label class="fl">MQTT Topic</label><input type="text" id="fpMqttTopic" placeholder="override topic (optional)"></div>
                <div class="hint" style="margin-top:4px">When enabled, the tag's value is published to the broker and inbound broker writes are applied to it. Leave the topic blank to use the default <span class="mono">{TopicPrefix}/{SourceId}/{DaItemId}</span> scheme.</div>
                <div class="field"><label class="fl">Influx log</label><input type="checkbox" id="fpInfluxEnabled"> <span class="msg">write this tag to InfluxDB</span></div>
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
        <button class="help-subtab" onclick="switchHelpSubTab('dashboard-tabs')">Dashboard Tabs</button>
        <button class="help-subtab" onclick="switchHelpSubTab('reference')">Reference</button>
    </div>
    <div class="help-subtab-content active" id="help-getting-started">
        <div class="help-accordion" id="helpContent1"><span class="msg">Loading help…</span></div>
    </div>
    <div class="help-subtab-content" id="help-dashboard-tabs">
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
    <div class="grid2">
        <div class="box">
            <div class="box-h">MQTT Broker <span class="info" data-tip="This app connects TO an external MQTT broker (like Mosquitto, HiveMQ, or AWS IoT). It does NOT include its own broker. Configure your broker connection here. Settings are saved to mqtt.json.">i</span></div>
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
                    <div class="field"><label class="fl" for="mqttPrefix">Topic Prefix</label><span class="info" data-tip="Prefix for all topics, e.g. bridge/tags. Publish topic = {prefix}/{sourceId}/{daItemId}; subscribe filter = {prefix}/#. A per-tag override topic can be set in the tag faceplate.">i</span><input type="text" id="mqttPrefix" placeholder="bridge/tags"></div>
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
    <div class="box" style="margin-top:14px">
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
    <div class="grid2">
        <div class="box">
            <div class="box-h">InfluxDB <span class="info" data-tip="This app writes to an external InfluxDB 2.x/3.x server. It does NOT run InfluxDB itself. Configure URL, Org, Bucket and Token here. Settings are saved to influx.json.">i</span></div>
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
</div>
<div class="view" id="view-diagram">
    <div class="diag-toolbar">
        <button class="diag-tab active" data-diag="all" onclick="showDiagTab('all')">All</button>
        <button class="diag-tab" data-diag="da-ua" onclick="showDiagTab('da-ua')">DA→UA</button>
        <button class="diag-tab" data-diag="da-da" onclick="showDiagTab('da-da')">DA to DA</button>
        <button class="diag-tab" data-diag="mqtt" onclick="showDiagTab('mqtt')">MQTT</button>
        <div class="diag-zoom" title="Ctrl+wheel zoom toward cursor · drag canvas to pan">
            <button type="button" class="diag-zoom-btn" id="diagZoomOut" title="Zoom out">&minus;</button>
            <span class="diag-zoom-label" id="diagZoomLabel">100%</span>
            <button type="button" class="diag-zoom-btn" id="diagZoomIn" title="Zoom in">+</button>
            <button type="button" class="diag-zoom-btn" id="diagZoomFit" title="Fit entire diagram">Fit</button>
            <button type="button" class="diag-zoom-btn" id="diagZoomFitW" title="Fit width">Fit W</button>
            <button type="button" class="diag-zoom-btn" id="diagZoomReset" title="Reset zoom">Reset</button>
        </div>
        <div class="diag-legend">
            <span class="legend-item"><span class="legend-dot good"></span>Good</span>
            <span class="legend-item"><span class="legend-dot warn"></span>Stale</span>
            <span class="legend-item"><span class="legend-dot bad"></span>Error</span>
            <span class="legend-item"><span class="legend-dot off"></span>Disabled</span>
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
    linkBrowsePath: '',
    sources: [],
    selectedSourceId: 'default',
    editingNewSource: false,
    liveValuesEnabled: true,
    lastValueCount: 0,
    updateRateMs: 1000,
    useSubscriptions: true,
    logsLoaded: false,
    appInfoLoaded: false,
    mappings: [],
    daLinks: [],
    linkDraft: { consumer: null, provider: null },
    mappingSort: 'name',
    mappingSortDir: 1,
    mappingFilter: '',
    mqttValFilter: { direction: '', topic: '' },
    valuesByKey: new Map(),
    handleHistory: [],
    handleBaseline: null,
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
    } else if (tab === 'da-da') {
        const result = renderDaDaDiagram();
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
    svg.innerHTML = html;
    applyDiagramZoom();
}

function linkEndpoints(link) {
    const providerSourceId = link.providerSourceId || link.ProviderSourceId || 'default';
    const providerItemId = link.providerItemId || link.ProviderItemId || link.providerDaItemId || link.ProviderDaItemId || '';
    const consumerSourceId = link.consumerSourceId || link.ConsumerSourceId || link.sourceId || link.SourceId || 'default';
    const consumerItemId = link.consumerItemId || link.ConsumerItemId || link.consumerDaItemId || link.ConsumerDaItemId || link.daItemId || link.DaItemId || '';
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

function collectDaLinks() {
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
    (state.daLinks || []).forEach(l => push(l, 'rule'));
    // legacy provider fields still present on mappings
    (state.mappings || []).forEach(m => {
        const pSid = m.providerSourceId || m.ProviderSourceId;
        const pItem = m.providerDaItemId || m.ProviderDaItemId || m.providerItemId || m.ProviderItemId;
        if (!pSid || !pItem) return;
        push({
            providerSourceId: pSid,
            providerItemId: pItem,
            consumerSourceId: m.sourceId || m.SourceId || 'default',
            consumerItemId: m.daItemId || m.DaItemId || '',
            enabled: (m.enabled ?? m.Enabled) !== false
        }, 'legacy');
    });
    return links;
}

function tagShortName(tagOrItemId) {
    if (tagOrItemId && typeof tagOrItemId === 'object') {
        const itemId = tagOrItemId.daItemId || tagOrItemId.DaItemId || '';
        const display = tagOrItemId.displayName || tagOrItemId.DisplayName || '';
        if (display) return String(display);
        return String(itemId).split('.').pop() || itemId || '?';
    }
    return String(tagOrItemId || '').split('.').pop() || String(tagOrItemId || '?');
}

function drawEdge(x1, y1, x2, y2, status, color) {
    return `<path class="diag-edge ${status}" d="M ${x1} ${y1} L ${x2} ${y2}" stroke="${color}"/>` +
           `<path class="diag-flow ${status}" d="M ${x1} ${y1} L ${x2} ${y2}" stroke="${color}"/>`;
}

function drawCurve(x1, y1, x2, y2, status, color, lift = 40) {
    const midX = (x1 + x2) / 2;
    const midY = Math.min(y1, y2) - lift;
    return `<path class="diag-edge ${status}" d="M ${x1} ${y1} Q ${midX} ${midY} ${x2} ${y2}" stroke="${color}"/>` +
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
    const links = collectDaLinks();

    if (mappings.length === 0 && sources.length === 0) {
        return { svg: '<text x="50%" y="50%" text-anchor="middle" fill="#6b7689" font-size="14">No sources or tags configured</text>', maxHeight: 600, maxWidth: 1400 };
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
    svg += `<text x="40" y="48" fill="#6b7689" font-size="10">Sources → tag groups → UA / MQTT · trunks colored by live status · detail on DA→UA / DA-to-DA / MQTT tabs</text>`;

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

    // Aggregate DA-to-DA by source pair
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
        return { svg: '<text x="50%" y="50%" text-anchor="middle" fill="#6b7689" font-size="14">No tags configured</text>', maxHeight: 600, maxWidth: 1100 };
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
    svg += `<text x="50" y="28" fill="#6b7689" font-size="11" font-weight="600">DA → UA (aggregated)</text>`;
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
                const itemId = tag.daItemId || tag.DaItemId || '';
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

function renderDaDaDiagram() {
    const mappings = state.mappings || [];
    const sources = state.sources || [];
    const links = collectDaLinks();

    if (mappings.length === 0 && links.length === 0) {
        return { svg: '<text x="50%" y="50%" text-anchor="middle" fill="#6b7689" font-size="14">No tags or DA-to-DA links configured</text>', maxHeight: 600, maxWidth: 1100 };
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
        return { svg: '<text x="50%" y="50%" text-anchor="middle" fill="#6b7689" font-size="14">No mapped tags</text>', maxHeight: 600, maxWidth: 1100 };
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
                const itemId = tag.daItemId || tag.DaItemId || '';
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
    const itemId = tag.daItemId || tag.DaItemId || '';
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
        tagKey(m.sourceId || m.SourceId || 'default', m.daItemId || m.DaItemId || '') === ep.providerKey);
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

function renderLiveValue(value) {
    if (!value) return '<span class="msg">No live value</span>';
    const text = String(get(value, 'value') ?? '');
    const quality = get(value, 'daQuality');
    const isGood = !!get(value, 'isGood');
    const timestamp = locTime(get(value, 'timestampUtc'));
    return `<div class="fp-k">Real value</div><div class="fp-v mono" title="${attr(text)}">${esc(text)}</div><div class="fp-meta"><span>${badge(isGood ? 'Good' : 'Bad', isGood ? 'good' : 'bad')} <span class="${isGood ? 'good' : 'bad'}">(${esc(String(quality ?? '—'))})</span></span><span class="timestamp">${esc(timestamp)}</span></div>`;
}

function linkTagLabel(sourceId, itemId, nameOverride = null) {
    const mapping = getMapping(sourceId, itemId);
    const name = nameOverride || (mapping ? (mapping.displayName || mapping.DisplayName || itemId) : itemId);
    return `${name} (${sourceId || 'default'} · ${itemId})`;
}
function renderLinkSourceStatus() {
    const sourceStatus = el('linkSourceStatus');
    if (!sourceStatus) return;
    const source = currentSource();
    if (source) {
        const cs = get(source, 'connectionState') || '—';
        sourceStatus.innerHTML = `${badge(cs, stateClass(cs))} <span class="msg">${esc(source.displayName || source.sourceId)} · ${esc(source.sourceId)}</span>`;
        return;
    }
    if (state.editingNewSource) {
        sourceStatus.innerHTML = '<span class="msg">Save the new source before browsing DA links.</span>';
        return;
    }
    sourceStatus.innerHTML = '<span class="msg">Select a saved source, then browse tags below.</span>';
}
function renderLinkDraftTarget(targetId, selection, emptyMessage) {
    el(targetId).innerHTML = selection
        ? `<span>${esc(linkTagLabel(selection.sourceId, selection.itemId, selection.name || null))}</span>`
        : `<span class="msg">${esc(emptyMessage)}</span>`;
}
function renderLinksView() {
    const links = state.daLinks || [];
    const consumer = state.linkDraft.consumer;
    const provider = state.linkDraft.provider;
    renderLinkSourceStatus();
    renderLinkDraftTarget('linkConsumerTarget', consumer, 'Browse the active source and choose a consumer tag.');
    renderLinkDraftTarget('linkProviderTarget', provider, 'Browse the active source and choose a provider tag.');
    el('btnSetLink').disabled = !(consumer && provider);
    el('btnClearLink').disabled = !(consumer && findDaLinkByConsumer(consumer.key));
    el('btnClearLinkSelection').disabled = !(consumer || provider);
    el('linksCount').textContent = links.length ? links.length + (links.length === 1 ? ' rule' : ' rules') : 'No rules';
    el('linksList').innerHTML = links.length ? links.map(link => {
        const consumerSourceId = link.consumerSourceId || link.ConsumerSourceId || 'default';
        const consumerItemId = link.consumerItemId || link.ConsumerItemId || '';
        const providerSourceId = link.providerSourceId || link.ProviderSourceId || 'default';
        const providerItemId = link.providerItemId || link.ProviderItemId || '';
        const linkId = link.id || link.Id || '';
        return `<div class="li"><div style="flex:1;min-width:0"><span class="n">${esc(linkTagLabel(consumerSourceId, consumerItemId))}</span></div><span class="pill" style="padding:1px 6px;font-size:10px;background:#e8f0fe;color:#1a73e8">⇠ fed by</span><div style="flex:1;min-width:0"><span class="n">${esc(linkTagLabel(providerSourceId, providerItemId))}</span></div><button class="btn ghost" type="button" data-action="unlink" data-link-id="${attr(linkId)}">Delete</button></div>`;
    }).join('') : '<span class="msg">No DA links yet. Browse the active source and pick a consumer/provider pair.</span>';
}
function findDaLinkByConsumer(consumerKey) {
    return (state.daLinks || []).find(link => tagKey(link.consumerSourceId || link.ConsumerSourceId || 'default', link.consumerItemId || link.ConsumerItemId || '') === consumerKey) || null;
}
async function saveDaLink(consumerKey, providerKey) {
    if (!consumerKey || !providerKey) { el('linksMessage').textContent = 'Pick both a consumer and a provider.'; return; }
    if (consumerKey === providerKey) { el('linksMessage').textContent = '✗ A tag cannot link to itself.'; return; }
    const [consumerSourceId, consumerItemId] = parseTagKey(consumerKey);
    const [providerSourceId, providerItemId] = parseTagKey(providerKey);
    const existing = findDaLinkByConsumer(consumerKey);
    const link = {
        id: existing ? (existing.id || existing.Id) : '00000000-0000-0000-0000-000000000000',
        providerSourceId: providerSourceId || 'default',
        providerItemId,
        consumerSourceId: consumerSourceId || 'default',
        consumerItemId,
        enabled: existing ? ((existing.enabled ?? existing.Enabled) !== false) : true
    };
    const url = existing ? '/api/da-links/' + encodeURIComponent(link.id) : '/api/da-links';
    const method = existing ? 'PUT' : 'POST';
    const r = await fetch(url, {
        method,
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ link })
    });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    el('linksMessage').textContent = existing ? '✓ DA link updated.' : '✓ DA link created.';
    await loadDaLinks();
}
async function deleteDaLink(linkId) {
    if (!linkId) { el('linksMessage').textContent = 'Pick a saved DA link to delete.'; return; }
    const r = await fetch('/api/da-links/' + encodeURIComponent(linkId), { method: 'DELETE' });
    const p = await r.json();
    if (!r.ok) throw new Error(p.error || ('HTTP ' + r.status));
    el('linksMessage').textContent = '✓ DA link removed.';
    await loadDaLinks();
}
function clearLinkDraftSelection() {
    state.linkDraft.consumer = null;
    state.linkDraft.provider = null;
    el('linksMessage').textContent = 'Selection cleared.';
    renderLinksView();
}
function renderMappingRow(mapping) {
    const sourceId = mapping.sourceId || mapping.SourceId || 'default';
    const item = mapping.daItemId || mapping.DaItemId;
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
    const deadbandBadge = deadband > 0 ? `<span class="pill" style="padding:1px 6px;font-size:10px">db ${deadband}%</span>` : '';
    const mqttOn = (mapping.mqttEnabled ?? mapping.MqttEnabled) === true;
    const mqttBadge = mqttOn ? `<span class="pill" style="padding:1px 6px;font-size:10px">MQTT</span>` : '';
    const influxOn = (mapping.influxEnabled ?? mapping.InfluxEnabled) === true;
    const influxBadge = influxOn ? `<span class="pill" style="padding:1px 6px;font-size:10px">Influx</span>` : '';
    const desc = (mapping.description || mapping.Description || '').trim();
    const descIcon = desc ? `<span class="li-desc" title="${attr(desc)}" data-action="open-faceplate" data-source-id="${attr(sourceId)}" data-item-id="${attr(item)}">&#8505;</span>` : '';
    return `<div class="li clickable" data-action="open-faceplate" data-source-id="${attr(sourceId)}" data-item-id="${attr(item)}">${descIcon}<div style="flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap"><span class="n">${esc(name)}</span> <span class="p">${esc(sourceId)} · ${esc(item)} · UA: ${esc(node)}</span></div><div class="li-badge">${accessBadge}${deadbandBadge}${rateBadge}${mqttBadge}${influxBadge}</div></div>`;
}

function renderMappingRows(mappings) {
    return mappings.length ? mappings.map(renderMappingRow).join('') : '<span class="msg">No DA → OPC UA mappings.</span>';
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
    el('fpPollRate').value = String(pollRate);
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
    el('fpLivePanel').innerHTML = renderLiveValue(currentValue(sourceId, itemId));
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

 async function showTab(name) {
     document.querySelectorAll('.tabbtn').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
    document.querySelectorAll('.view').forEach(v => v.classList.toggle('active', v.id === 'view-' + name));
    if (location.hash !== '#' + name) history.replaceState(null, '', '#' + name);
    if (name === 'logs') { state.logsLoaded = false; loadLogs(true).catch(e => el('logMessage').textContent = '✗ ' + e.message); }
    if (name === 'diagnostics') { diagnosticsActive = true; loadDiagnostics(); }
    else { diagnosticsActive = false; }
    if (name === 'about') loadAppInfo().catch(e => el('aboutName').textContent = '✗ ' + e.message);
    if (name === 'help') loadHelp().catch(e => el('helpContent').innerHTML = '<span class="msg bad">✗ ' + esc(e.message) + '</span>');
        if (name === 'mqtt') { await loadMqtt(); await loadMqttValues(); }
    if (name === 'influx') { await loadInflux(); }
    if (name === 'links') loadDaLinks().catch(e => el('linksMessage').textContent = '✗ ' + e.message);
    if (name === 'diagram') {
        state.diagramLoaded = true;
        await Promise.all([loadSources(), loadMappings(), loadDaLinks(), loadMqtt().catch(() => {})]);
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
    return new Date(u).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}
function locTime(u) { return u ? new Date(u).toLocaleString() : '—'; }
function get(o, k) { return o?.[k] ?? o?.[k[0].toUpperCase() + k.slice(1)]; }
function currentSource() { return state.editingNewSource ? null : state.sources.find(s => s.sourceId === state.selectedSourceId) || null; }
function defaultUaNodeId(sourceId, itemId) { return `ns=2;s=${sourceId}/${itemId}`; }
function renderSources() {
    const select = el('selectedSource');
    const mapSelect = el('mapSourceSelect');
    const options = state.sources.map(source => `<option value="${esc(source.sourceId)}">${esc(source.displayName || source.sourceId)}</option>`).join('');
    select.innerHTML = options;
    mapSelect.innerHTML = options;
    if (!state.editingNewSource && !state.sources.some(source => source.sourceId === state.selectedSourceId) && state.sources.length) {
        state.selectedSourceId = state.sources[0].sourceId;
    }
    select.value = state.selectedSourceId;
    mapSelect.value = state.selectedSourceId;
    el('pSources').textContent = state.sources.length;
    const sideCount = el('pSourcesSide'); if (sideCount) sideCount.textContent = state.sources.length + ' source' + (state.sources.length !== 1 ? 's' : '');
    el('sourcesList').innerHTML = state.sources.length ? state.sources.map(source =>
        `<div class="li source-row"><div><div class="n">${esc(source.displayName || source.sourceId)}</div><div class="p">${esc(source.sourceId)} · ${esc(source.host || 'localhost')} · ${esc(source.progId || '')} · ${formatMs(source.updateRateMs)}</div></div><button class="btn ghost" data-action="select-source" data-source-id="${attr(source.sourceId)}">Select</button></div>`
    ).join('') : '<span class="msg">No sources configured.</span>';
    loadSelectedSourceForm();
}
function loadSelectedSourceForm() {
    const source = currentSource();
    if (!source) return;
    state.editingNewSource = false;
    el('cfgSourceId').value = source.sourceId || '';
    el('cfgSourceId').disabled = true;
    el('cfgDisplayName').value = source.displayName || '';
    el('cfgProgId').value = source.progId || '';
    el('cfgHost').value = source.host || 'localhost';
    el('cfgUser').value = source.remoteUsername || '';
    el('cfgPass').value = '';
    el('cfgDomain').value = source.remoteDomain || '';
    el('cfgMessage').textContent = 'Editing ' + (source.displayName || source.sourceId) + '.';
    hideSaveReset();
}
async function loadSources() {
    const payload = await (await fetch('/api/da/sources', { cache: 'no-store' })).json();
    state.sources = payload.sources || [];
    state.updateRateMs = Number(payload.updateRateMs || state.updateRateMs || 1000);
    state.useSubscriptions = payload.useSubscriptions !== false;
    el('cfgUseSubscriptions').checked = state.useSubscriptions;
    if (document.activeElement !== el('cfgUpdateRate')) el('cfgUpdateRate').value = String(state.updateRateMs);
    renderSources();
    if (document.getElementById('view-links')?.classList.contains('active')) renderLinksView();
}
function updateLiveValuesUi() {
    el('toggleLiveValues').textContent = state.liveValuesEnabled ? 'Disable Live Data' : 'Enable Live Data';
    el('valCount').textContent = state.lastValueCount + ' values' + (state.liveValuesEnabled ? '' : ' · paused');
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
async function loadDiagnostics() {
    if (!diagnosticsActive) return;
    try {
        const p = await (await fetch('/api/diagnostics', { cache: 'no-store' })).json();
        renderDiagnostics(p);
    } catch (e) {
        el('diagDaSources').innerHTML = '<span class="bad">✗ ' + esc(e.message) + '</span>';
    }
}

function renderDiagnostics(p) {
    // DA Source Diagnostics — reuse state data from /api/dashboard
    const sources = state.sources || [];
    const rateGroups = (state.rateGroups || []);
    const daHtml = sources.length ? sources.map(src => {
        const sid = get(src, 'sourceId') || 'default';
        const conn = get(src, 'connectionState') || '—';
        const latency = formatMs(get(src, 'lastDaReadDurationMs'));
        const srcGroups = rateGroups.filter(g => g.sourceId === sid);
        const totalTags = srcGroups.reduce((sum, g) => sum + (g.tagCount || 0), 0);
        const groupRows = srcGroups.length ? srcGroups.map(g => {
            const budget = Math.round(g.cycleBudgetPct || 0);
            const budgetCls = budget >= 80 ? 'bad' : (budget >= 50 ? 'warn' : 'good');
            return `<div class="li"><div style="flex:1"><div class="n">${formatMs(g.rateMs)} · ${g.tagCount} tags</div><div class="p">budget <span class="${budgetCls}">${budget}%</span> · limit ${g.tagLimit || '—'}</div></div></div>`;
        }).join('') : '<span class="msg">No rate groups.</span>';
        return `<div class="li"><div style="flex:1"><div class="n">${esc(get(src,'displayName') || sid)} ${badge(conn, stateClass(conn))}</div><div class="p">Latency: ${latency} · ${totalTags} tags in ${srcGroups.length} group(s)</div></div></div>${groupRows}`;
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
        return `<div class="li"><div style="flex:1"><div class="n">${esc(s.clientName || 'anonymous')}</div><div class="p">${s.subscriptions} subs · ${s.monitoredItems} monitored · ${s.publishRequestsInQueue} publish queued · ${s.totalPublishCount} total publishes</div><div class="p">last contact ${last}</div></div></div>`;
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
        return `<div class="li"><div style="flex:1"><div class="n">${esc(t.sourceId)} ${aliveBadge}</div><div class="p">queued: ${t.queuedItems} · last action ${last}</div></div></div>`;
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
function renderMarkdown(md) {
    const lines = md.replace(/\r\n/g, '\n').split('\n');
    let html = '', inList = false, inTable = false, inCode = false, tableHeader = false;
    const closeList = () => { if (inList) { html += '</ul>'; inList = false; } };
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
        if (/^#\s+/.test(line)) { closeList(); closeTable(); html += `<h1>${line.replace(/^#\s+/, '')}</h1>`; continue; }
        if (/^##\s+/.test(line)) { closeList(); closeTable(); html += `<h2>${line.replace(/^##\s+/, '')}</h2>`; continue; }
        if (/^###\s+/.test(line)) { closeList(); closeTable(); html += `<h3>${line.replace(/^###\s+/, '')}</h3>`; continue; }
        if (/^####\s+/.test(line)) { closeList(); closeTable(); html += `<h4>${line.replace(/^####\s+/, '')}</h4>`; continue; }
        if (/^\*\s+|^-\s+/.test(line)) { closeTable(); if (!inList) { html += '<ul>'; inList = true; } html += `<li>${line.replace(/^\*\s+|^-\s+/, '')}</li>`; continue; }
        closeList();
        if (/^\|/.test(line)) {
            if (line.replace(/\s/g, '').match(/^\|[-:|]+\|$/)) { tableHeader = true; continue; }
            const cells = line.split('|').filter((_, j, a) => j > 0 && j < a.length - 1).map(c => c.trim());
            if (!inTable) { html += '<table><thead><tr>'; html += cells.map(c => `<th>${c}</th>`).join(''); html += '</tr></thead><tbody>'; inTable = true; tableHeader = false; }
            else if (tableHeader) { tableHeader = false; continue; }
            else { html += '<tr>' + cells.map(c => `<td>${c}</td>`).join('') + '</tr>'; }
            continue;
        }
        closeTable();
        if (line.trim() === '') continue;
        if (/^\*/.test(line) && /\*$/.test(line)) { html += `<p><em>${line.replace(/^\*|\*$/g, '')}</em></p>`; }
        else { html += `<p>${line}</p>`; }
    }
    closeList(); closeTable();
    if (inCode) html += '</code></pre>';
    return html.replace(/\*\*(.+?)\*\*/g, '<b>$1</b>').replace(/`(.+?)`/g, '<code>$1</code>');
}
async function loadHelp() {
    if (helpLoaded) return;
    const p = await (await fetch('/api/help', { cache: 'no-store' })).json();
    const groups = (p.markdown || '').split(/\r?\n===\r?\n/).filter(s => s.trim());
    
    const renderGroup = (groupMarkdown, containerId, openCount = 2) => {
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
    
    renderGroup(groups[0] || '', 'helpContent1', 2);
    renderGroup(groups[1] || '', 'helpContent2', 2);
    renderGroup(groups[2] || '', 'helpContent3', 1);
    
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

async function refresh() {
    try {
        const p = await (await fetch('/api/dashboard', { cache: 'no-store' })).json();
        const b = p.bridge || p.Bridge || {};
        const ua = p.ua || p.Ua || {};
        const vs = p.values || p.Values || [];
         const sources = get(b, 'sources') || [];
         const apps = p.apps || p.Apps || {};
         el('dot').className = 'dot';
         el('clock').textContent = new Date().toLocaleTimeString();
         el('pBridge').innerHTML = badge(get(b, 'bridgeState') || '—', stateClass(get(b, 'bridgeState')));
         el('pDa').innerHTML = badge(get(b, 'daConnectionState') || '—', stateClass(get(b, 'daConnectionState')));
         el('pUa').innerHTML = badge(get(ua, 'state') || '—', stateClass(get(ua, 'state')));
         el('pTags').textContent = get(b, 'mappingCount') ?? 0;
         el('pApps').textContent = get(apps, 'detectedCount') ?? 1;
        el('bridgeState').innerHTML = badge(get(b, 'bridgeState') || '—', stateClass(get(b, 'bridgeState')));
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
        state.valuesByKey = new Map(vs.map(v => [valueKey(get(v, 'sourceId') || 'default', get(v, 'daItemId')), v]));
        updateFaceplateLiveValues();
        if (state.diagramLoaded && document.querySelector('.tabbtn.active')?.dataset.tab === 'diagram') {
            renderDiagram();
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
        el('sourceStatusList').innerHTML = sources.length ? sources.map(source => {
            const connState = get(source,'connectionState') || '—';
            const connClass = stateClass(connState);
            return `<div class="li"><div style="flex:1"><div class="n">${esc(get(source,'displayName') || get(source,'sourceId'))} ${badge(connState, connClass)}</div><div class="p">${esc(get(source,'sourceId'))} · ${esc(get(source,'host') || '')} · ${esc(get(source,'progId') || '')}</div><div class="p">${formatMs(get(source,'updateRateMs'))} · ${(get(source,'lastDaReadCount') ?? 0)} values in ${formatMs(get(source,'lastDaReadDurationMs'))}${get(source,'lastError') ? ' · <span class="bad">' + esc(get(source,'lastError')) + '</span>' : ''}</div></div></div>`;
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

                if (resA && resAD) {
                    const gdi = Number(res.gdiObjects ?? 0);
                    const user = Number(res.userObjects ?? 0);
                    const baseline = state.handleBaseline ?? hc;
                    const drift = hc - baseline;
                    const gdiPct = (gdi / 10000) * 100;
                    const userPct = (user / 10000) * 100;

                    // Determine growth trend from history (compare first quarter to last quarter avg)
                    let trend = 'stable';
                    let trendPct = 0;
                    if (state.handleHistory.length >= 12) {
                        const q = Math.floor(state.handleHistory.length / 4);
                        const earlyAvg = state.handleHistory.slice(0, q).reduce((a, b) => a + b, 0) / q;
                        const recentAvg = state.handleHistory.slice(-q).reduce((a, b) => a + b, 0) / q;
                        trendPct = earlyAvg > 0 ? ((recentAvg - earlyAvg) / earlyAvg) * 100 : 0;
                        if (trendPct > 15) trend = 'rising';
                        else if (trendPct < -5) trend = 'falling';
                    }

                    let verdict, cls, detail;
                    if (gdiPct >= 80 || userPct >= 80) {
                        verdict = 'Critical'; cls = 'bad';
                        detail = 'GDI/USER near 10,000 limit — restart the app to avoid crash.';
                    } else if (gdiPct >= 50 || userPct >= 50) {
                        verdict = 'Warning'; cls = 'warn';
                        detail = 'GDI/USER above 50% of the 10,000 per-process limit.';
                    } else if (drift > 200 && trend === 'rising') {
                        verdict = 'Watch'; cls = 'warn';
                        detail = 'Handle count rising (+' + Math.round(trendPct) + '% trend, +' + drift + ' since start). Possible leak.';
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
        state.lastValueCount = vs.length;
        updateLiveValuesUi();
        if (state.liveValuesEnabled) {
            el('values').innerHTML = vs.length ? vs.map(it => {
                const g = get(it, 'isGood');
                const q = get(it, 'daQuality');
                const sourceId = get(it, 'sourceId');
                const itemId = get(it, 'daItemId');
                const value = String(get(it, 'value') ?? '');
                const timestamp = locTime(get(it, 'timestampUtc'));
                const timestampShort = shortTime(get(it, 'timestampUtc'));
                return `<tr><td><code title="${attr(sourceId)}">${esc(sourceId)}</code></td><td><code title="${attr(itemId)}">${esc(itemId)}</code></td><td class="mono" title="${attr(value)}">${esc(value)}</td><td title="${attr(String(q ?? ''))}"><span class="quality">${badge(g ? 'Good' : 'Bad', g ? 'good' : 'bad')} <span class="${g ? 'good' : 'bad'}">(${q})</span></span></td><td class="msg timestamp" title="${attr(timestamp)}">${esc(timestampShort)}</td></tr>`;
            }).join('') : '<tr><td colspan="5" class="msg">No values yet.</td></tr>';
        }
    } catch (e) {
        el('dot').className = 'dot off';
        el('clock').textContent = 'offline';
        if (state.liveValuesEnabled) {
            el('values').innerHTML = `<tr><td colspan="5" class="bad">${esc(e.message)}</td></tr>`;
        }
    }
}
async function loadDaLinks() {
    const p = await (await fetch('/api/da-links', { cache: 'no-store' })).json();
    state.daLinks = p.links || [];
    if (document.getElementById('view-links')?.classList.contains('active')) renderLinksView();
}

async function loadMappings() {
    const p = await (await fetch('/api/mappings', { cache: 'no-store' })).json();
    const mappings = p.mappings || [];
    state.mappings = mappings;
    const view = applyMappingView(mappings);
    el('mappedList').innerHTML = renderMappingRows(view);
    if (el('mapCount')) el('mapCount').textContent = view.length + (view.length !== mappings.length ? ' / ' + mappings.length + ' mappings' : ' mappings');
    refreshTagBrowserMappedBadges();
    if (document.getElementById('view-links')?.classList.contains('active')) renderLinksView();
}
function refreshTagBrowserMappedBadges() {
    const tree = el('tagTree');
    if (!tree) return;
    const mappedKeys = new Set((state.mappings || []).map(m => valueKey(m.sourceId || m.SourceId || 'default', m.daItemId || m.DaItemId)));
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
    } catch (e) { /* ignore */ }
}
async function loadMqttStatus() {
    try {
        const st = await (await fetch('/api/mqtt/status', { cache: 'no-store' })).json();
        state.mqttConnectionState = st.state || 'Disconnected';
        if (el('mqttState')) {
            el('mqttState').textContent = st.state || 'Disconnected';
            el('mqttState').className = 'v ' + (st.state === 'Connected' ? 'badge good' : 'badge bad');
        }
        if (el('mqttLastError')) el('mqttLastError').textContent = st.lastError || 'No errors';
        if (el('mqttPublished')) el('mqttPublished').textContent = (st.publishedCount || 0).toLocaleString();
        if (el('mqttReceived')) el('mqttReceived').textContent = (st.receivedCount || 0).toLocaleString();
        if (el('mqttPublishedRate')) el('mqttPublishedRate').textContent = (st.publishedRate || 0).toFixed(1) + '/s';
        if (el('mqttReceivedRate')) el('mqttReceivedRate').textContent = (st.receivedRate || 0).toFixed(1) + '/s';
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
    } catch (e) { /* ignore */ }
}
async function loadInfluxStatus() {
    try {
        const st = await (await fetch('/api/influx/status', { cache: 'no-store' })).json();
        if (el('influxState')) {
            el('influxState').textContent = st.state || 'Disconnected';
            el('influxState').className = 'v ' + (st.state === 'Connected' ? 'badge good' : 'badge bad');
        }
        if (el('influxLastError')) el('influxLastError').textContent = st.lastError || 'No errors';
        if (el('influxWritten')) el('influxWritten').textContent = (st.writtenCount || 0).toLocaleString();
        if (el('influxWrittenRate')) el('influxWrittenRate').textContent = (st.writtenRate || 0).toFixed(1) + '/s';
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
async function disconnectInflux() {
    await fetch('/api/influx/disconnect', { method: 'POST' });
    el('influxMessage').textContent = 'Disconnected.';
    await loadInflux();
}
function applyMappingView(mappings) {
    const filter = (state.mappingFilter || '').trim().toLowerCase();
    let view = mappings;
    if (filter) {
        view = mappings.filter(m => {
            const sourceId = m.sourceId || m.SourceId || 'default';
            const item = m.daItemId || m.DaItemId || '';
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
            case 'item': av = (a.daItemId || a.DaItemId || ''); bv = (b.daItemId || b.DaItemId || ''); break;
            case 'node': av = (a.uaNodeId || a.UaNodeId || ''); bv = (b.uaNodeId || b.UaNodeId || ''); break;
            case 'access': av = accessRank(a); bv = accessRank(b); break;
            case 'rate': av = (a.pollRateMs ?? a.PollRateMs ?? 0); bv = (b.pollRateMs ?? b.PollRateMs ?? 0); break;
            case 'deadband': av = Number(a.deadbandPct ?? a.DeadbandPct ?? 0); bv = Number(b.deadbandPct ?? b.DeadbandPct ?? 0); break;
            case 'status': av = ((a.enabled ?? a.Enabled) !== false) ? 0 : 1; bv = ((b.enabled ?? b.Enabled) !== false) ? 0 : 1; break;
            case 'description': av = (a.description || a.Description || ''); bv = (b.description || b.Description || ''); break;
            default: av = (a.displayName || a.DisplayName || a.daItemId || a.DaItemId || ''); bv = (b.displayName || b.DisplayName || b.daItemId || b.DaItemId || '');
        }
        if (typeof av === 'number' && typeof bv === 'number') return (av - bv) * dir;
        return String(av).localeCompare(String(bv), undefined, { numeric: true, sensitivity: 'base' }) * dir;
    };
    return view.slice().sort(cmp);
}
function rerenderMappings() {
    const view = applyMappingView(state.mappings || []);
    el('mapCount').textContent = view.length + (view.length !== (state.mappings || []).length ? ' / ' + (state.mappings || []).length + ' mappings' : ' mappings');
    el('mappedList').innerHTML = renderMappingRows(view);
}

function getMapping(sourceId, itemId) {
    return state.mappings.find(mapping => {
        const mappingSourceId = mapping.sourceId || mapping.SourceId || 'default';
        const mappingItemId = mapping.daItemId || mapping.DaItemId;
        return mappingSourceId === sourceId && mappingItemId === itemId;
    }) || null;
}



async function updateMapping(sourceId, itemId, mutate) {
    const mapping = getMapping(sourceId, itemId);
    if (!mapping) throw new Error('Mapping not found.');
    const payload = {
        sourceId,
        daItemId: itemId,
        displayName: mapping.displayName || mapping.DisplayName || itemId,
        description: mapping.description ?? mapping.Description ?? null,
        dataType: mapping.dataType || mapping.DataType || 'Auto',
        uaNodeId: mapping.uaNodeId || mapping.UaNodeId || defaultUaNodeId(sourceId, itemId),
        enabled: (mapping.enabled ?? mapping.Enabled) !== false,
        mode: mapping.mode || mapping.Mode || 'Source',
        manualValue: mapping.manualValue ?? mapping.ManualValue ?? null,
        pollRateMs: mapping.pollRateMs ?? mapping.PollRateMs ?? 0,
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

function pickSource(sourceId) {
    state.selectedSourceId = sourceId;
    state.editingNewSource = false;
    state.tagPath = '';
    el('tagTree').innerHTML = '<span class="msg">Browse the active source to load tags.</span>';
    el('tagStatus').textContent = 'Browse all tags, or open folders one level at a time.';
    renderCrumb();
    resetLinkBrowser();
    renderSources();
    if (document.getElementById('view-links')?.classList.contains('active')) renderLinksView();
}
async function saveSource() {
    const sourceId = el('cfgSourceId').value.trim();
    if (!sourceId) {
        el('cfgMessage').textContent = '✗ Source ID is required.';
        return;
    }
    const body = {
        sourceId,
        displayName: el('cfgDisplayName').value.trim() || null,
        progId: el('cfgProgId').value.trim(),
        host: el('cfgHost').value.trim() || 'localhost',
        remoteUsername: el('cfgUser').value.trim() || null,
        remotePassword: el('cfgPass').value || null,
        remoteDomain: el('cfgDomain').value.trim() || null
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
    if (!source || state.editingNewSource) return;
    if (!confirm('Remove source "' + source.sourceId + '" and its DA → OPC UA mappings?')) return;
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
function resetSource() {
    if (state.editingNewSource) { newSource(); return; }
    loadSelectedSourceForm();
    el('cfgMessage').textContent = 'Reverted to saved values.';
}

function newSource() {
    state.selectedSourceId = '';
    state.editingNewSource = true;
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
    showSaveReset();
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
function renderLinkCrumb() {
    const bc = el('linkBrowseBreadcrumb');
    if (!state.linkBrowsePath) {
        bc.innerHTML = '<span class="current">root</span>';
        return;
    }
    const parts = state.linkBrowsePath.split('.');
    let html = '<a data-link-crumb="">root</a><span class="sep">/</span>';
    let acc = '';
    for (let i = 0; i < parts.length; i++) {
        acc = acc ? acc + '.' + parts[i] : parts[i];
        if (i < parts.length - 1) {
            html += `<a data-link-crumb="${attr(acc)}">${esc(parts[i])}</a><span class="sep">/</span>`;
        } else {
            html += `<span class="current">${esc(parts[i])}</span>`;
        }
    }
    bc.innerHTML = html;
}
function resetLinkBrowser() {
    state.linkBrowsePath = '';
    renderLinkCrumb();
    el('linkBrowseTree').innerHTML = '<span class="msg">Use the active source to browse tags for DA links.</span>';
    el('linkBrowseStatus').textContent = 'Use the active source selection from Connection or Tags, then pick consumer/provider tags.';
}
function setLinkDraftSelection(role, sourceId, itemId, name) {
    state.linkDraft[role] = {
        key: tagKey(sourceId, itemId),
        sourceId: sourceId || 'default',
        itemId,
        name: name || itemId
    };
    const roleName = role === 'consumer' ? 'Consumer' : 'Provider';
    el('linksMessage').textContent = roleName + ' selected from source ' + (sourceId || 'default') + '.';
    renderLinksView();
}
async function browseLinkTags(path, recursive = false) {
    const source = currentSource();
    if (!source || state.editingNewSource) {
        el('linkBrowseTree').innerHTML = '<span class="msg">Select or save a source before browsing DA links.</span>';
        el('linkBrowseBreadcrumb').innerHTML = '';
        return;
    }
    state.linkBrowsePath = path || '';
    renderLinkCrumb();
    el('linkBrowseTree').innerHTML = '<span class="msg">Browsing…</span>';
    el('linkBrowseStatus').textContent = recursive ? 'Loading all tags…' : 'Loading folder…';
    const body = {
        sourceId: source.sourceId,
        progId: source.progId,
        host: source.host || 'localhost',
        path: state.linkBrowsePath,
        recursive,
        remoteUsername: source.remoteUsername || null,
        remotePassword: null,
        remoteDomain: source.remoteDomain || null
    };
    const p = await (await fetch('/api/da/tags', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })).json();
    if (p.error) throw new Error(p.error);
    const branches = p.branches || [];
    const tags = p.tags || [];
    const rows = [];
    if (state.linkBrowsePath) {
        const parent = state.linkBrowsePath.includes('.') ? state.linkBrowsePath.substring(0, state.linkBrowsePath.lastIndexOf('.')) : '';
        rows.push(`<div class="li clickable" data-action="open-link-branch" data-path="${attr(parent)}"><span class="icon folder">&#9650;</span><div style="flex:1"><div class="n">..</div><div class="p">Up one level</div></div></div>`);
    }
    for (const branch of branches) {
        const child = state.linkBrowsePath ? state.linkBrowsePath + '.' + branch : branch;
        rows.push(`<div class="li clickable" data-action="open-link-branch" data-path="${attr(child)}"><span class="icon folder">&#128193;</span><div style="flex:1"><div class="n">${esc(branch)}</div><div class="p">folder</div></div></div>`);
    }
    for (const tag of tags) {
        const itemId = tag.itemId || tag.ItemId;
        const name = tag.name || tag.Name || itemId;
        const key = tagKey(source.sourceId, itemId);
        const existing = findDaLinkByConsumer(key);
        const isConsumer = state.linkDraft.consumer && state.linkDraft.consumer.key === key;
        const isProvider = state.linkDraft.provider && state.linkDraft.provider.key === key;
        rows.push(`<div class="li"><span class="icon tag">&#9878;</span><div style="flex:1"><div class="n">${esc(name)}</div><div class="p">${esc(itemId)}</div></div><div class="li-actions">${existing ? '<span class="mapped-badge">Linked consumer</span>' : ''}${isConsumer ? '<span class="pill" style="padding:1px 6px;font-size:10px">Consumer</span>' : ''}${isProvider ? '<span class="pill" style="padding:1px 6px;font-size:10px">Provider</span>' : ''}<button class="btn ghost" data-action="pick-link-consumer" data-source-id="${attr(source.sourceId)}" data-item-id="${attr(itemId)}" data-name="${attr(name)}">Consumer</button><button class="btn ghost" data-action="pick-link-provider" data-source-id="${attr(source.sourceId)}" data-item-id="${attr(itemId)}" data-name="${attr(name)}">Provider</button></div></div>`);
    }
    el('linkBrowseTree').innerHTML = rows.length ? rows.join('') : '<span class="msg">No tags or folders here.</span>';
    el('linkBrowseStatus').textContent = branches.length + ' folders · ' + tags.length + ' tags';
    renderLinksView();
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
async function browseTags(path, recursive = false) {
    const source = currentSource();
    if (!source || state.editingNewSource) {
        el('tagTree').innerHTML = '<span class="msg">Select or save a source before browsing tags.</span>';
        el('tagBreadcrumb').innerHTML = '';
        return;
    }
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
    const mappedKeys = new Set((state.mappings || []).map(m => valueKey(m.sourceId || m.SourceId || 'default', m.daItemId || m.DaItemId)));
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
        const itemId = tag.itemId || tag.ItemId;
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
        body: JSON.stringify({ tags: [{ sourceId, daItemId: itemId, displayName: name || itemId, dataType: 'Auto', uaNodeId: defaultUaNodeId(sourceId, itemId) }] })
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
        body: JSON.stringify({ tags: [{ sourceId, daItemId: itemId, displayName: itemId, dataType: 'Auto', uaNodeId }] })
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
        body: JSON.stringify({ sourceId, daItemId: itemId })
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
            el('values').innerHTML = `<tr><td colspan="5" class="bad">${esc(e.message)}</td></tr>`;
        });
    }
}

function bindDynamicButtons() {
    el('sourcesList').addEventListener('click', event => {
        const button = event.target.closest('button[data-action="select-source"]');
        if (!button) return;
        pickSource(button.dataset.sourceId || '');
    });
    el('listServers').addEventListener('click', event => {
        const button = event.target.closest('button[data-action="pick-server"]');
        if (!button) return;
        pickServer(button.dataset.progId || '', button.dataset.host || 'localhost');
    });
    el('tagTree').addEventListener('click', event => {
        const actionEl = event.target.closest('[data-action]');
        if (!actionEl) return;
        if (actionEl.dataset.action === 'open-branch') {
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
        browseTags(link.dataset.crumb || '').catch(e => el('tagTree').innerHTML = `<span class="bad">${esc(e.message)}</span>`);
    });
    el('linkBrowseTree').addEventListener('click', event => {
        const actionEl = event.target.closest('[data-action]');
        if (!actionEl) return;
        if (actionEl.dataset.action === 'open-link-branch') {
            browseLinkTags(actionEl.dataset.path || '').catch(e => el('linkBrowseTree').innerHTML = `<span class="bad">${esc(e.message)}</span>`);
            return;
        }
        if (actionEl.tagName === 'BUTTON' && actionEl.dataset.action === 'pick-link-consumer') {
            setLinkDraftSelection('consumer', actionEl.dataset.sourceId || '', actionEl.dataset.itemId || '', actionEl.dataset.name || '');
            return;
        }
        if (actionEl.tagName === 'BUTTON' && actionEl.dataset.action === 'pick-link-provider') {
            setLinkDraftSelection('provider', actionEl.dataset.sourceId || '', actionEl.dataset.itemId || '', actionEl.dataset.name || '');
        }
    });
    el('linkBrowseBreadcrumb').addEventListener('click', event => {
        const link = event.target.closest('a[data-link-crumb]');
        if (!link) return;
        browseLinkTags(link.dataset.linkCrumb || '').catch(e => el('linkBrowseTree').innerHTML = `<span class="bad">${esc(e.message)}</span>`);
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
                payload.pollRateMs = Number.parseInt(el('fpPollRate').value, 10) || 0;
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
    });
}



document.addEventListener('DOMContentLoaded', async () => {
    bindDiagramPanZoom();
    el('selectedSource').addEventListener('change', e => pickSource(e.target.value));
    el('mapSourceSelect').addEventListener('change', e => pickSource(e.target.value));
    el('cfgApply').addEventListener('click', () => saveSource().catch(e => el('cfgMessage').textContent = '✗ ' + e.message));
    el('cfgReset').addEventListener('click', resetSource);
    el('cfgNew').addEventListener('click', newSource);
    el('cfgRemove').addEventListener('click', () => removeSelectedSource().catch(e => el('cfgMessage').textContent = '✗ ' + e.message));
    ['cfgSourceId','cfgDisplayName','cfgProgId','cfgHost','cfgUser','cfgPass','cfgDomain'].forEach(id => {
        el(id).addEventListener('input', () => { if (!state.editingNewSource) showSaveReset(); });
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
    el('btnRefreshLogs').addEventListener('click', () => loadLogs(true).catch(e => el('logMessage').textContent = '✗ ' + e.message));
    el('logLevel').addEventListener('change', () => {
        state.logsLoaded = false;
        loadLogs(true).catch(e => el('logMessage').textContent = '✗ ' + e.message);
    });
    el('logLimit').addEventListener('change', () => {
        state.logsLoaded = false;
        loadLogs(true).catch(e => el('logMessage').textContent = '✗ ' + e.message);
    });
    el('btnBrowseLinkTags').addEventListener('click', () => browseLinkTags('').catch(e => el('linkBrowseTree').innerHTML = `<span class="bad">${esc(e.message)}</span>`));
    el('btnBrowseAllLinkTags').addEventListener('click', () => browseLinkTags('', true).catch(e => el('linkBrowseTree').innerHTML = `<span class="bad">${esc(e.message)}</span>`));
    el('btnSetLink').addEventListener('click', () => saveDaLink(state.linkDraft.consumer ? state.linkDraft.consumer.key : '', state.linkDraft.provider ? state.linkDraft.provider.key : '').catch(e => el('linksMessage').textContent = '✗ ' + e.message));
    el('btnClearLink').addEventListener('click', () => {
        const consumerKey = state.linkDraft.consumer ? state.linkDraft.consumer.key : '';
        const existing = findDaLinkByConsumer(consumerKey);
        deleteDaLink(existing ? (existing.id || existing.Id || '') : '').catch(e => el('linksMessage').textContent = '✗ ' + e.message);
    });
    el('btnClearLinkSelection').addEventListener('click', () => clearLinkDraftSelection());
    el('linksList').addEventListener('click', event => {
        const btn = event.target.closest('button[data-action="unlink"]');
        if (!btn) return;
        deleteDaLink(btn.dataset.linkId || '').catch(e => el('linksMessage').textContent = '✗ ' + e.message);
    });
    bindDynamicButtons();
    const initTab = location.hash.slice(1);
    if (['monitor','connection','diagnostics','tags','links','logs','mqtt','influx','help','about'].includes(initTab)) showTab(initTab);
    await loadSources();
    await loadMappings();
    updateLiveValuesUi();
    await refresh();
    setInterval(refresh, 1000);
    setInterval(() => { if (el('logAutoRefresh')?.checked && document.querySelector('#view-logs.active')) { state.logsLoaded = false; loadLogs(true).catch(() => {}); } }, 3000);
    setInterval(() => { if (diagnosticsActive) loadDiagnostics().catch(() => {}); }, 2000);
    setInterval(() => {
        if (document.querySelector('#view-mqtt.active')) {
            loadMqttStatus().catch(() => {});
            if (el('mqttValAuto')?.checked) loadMqttValues().catch(() => {});
        }
        if (document.querySelector('#view-influx.active')) loadInfluxStatus().catch(() => {});
    }, 2000);
    if (initTab === 'logs') await loadLogs();
    if (initTab === 'help') await loadHelp();
    if (initTab === 'about') await loadAppInfo();
    fetch('/api/version').then(r => r.json()).then(p => { const v = (p.informationalVersion || p.version || '0.0.0').split('+')[0]; el('appVersion').textContent = 'v' + v; }).catch(() => {});
});
</script>
</body>
</html>
""";

    public static string FullHtml => Html + Script;
}
