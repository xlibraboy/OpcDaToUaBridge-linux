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
        body { background: var(--bg); color: var(--text); font-size: 13px; }
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
.app-shell { display: flex; min-height: calc(100vh - 46px); }
.tabbar { display: flex; flex-direction: column; background: var(--panel); border-right: 1px solid var(--border2); padding: 8px 0; width: 152px; flex-shrink: 0; }
.tabbtn { background: none; border: none; color: var(--muted); padding: 11px 16px; font-size: 13px; font-weight: 500; cursor: pointer; border-left: 3px solid transparent; display: flex; align-items: center; gap: 8px; text-align: left; }
.tabbtn:hover { color: var(--text); background: var(--panel2); }
.tabbtn.active { color: var(--accent); border-left-color: var(--accent); background: var(--panel2); }
.content { flex: 1; min-width: 0; overflow-x: auto; }
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
        .fp-body { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
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
        .conn-side { display: flex; flex-direction: column; gap: 14px; }
        .conn-section { padding: 10px 0; border-top: 1px solid var(--border); }
        .conn-section:first-of-type { border-top: none; padding-top: 4px; }
        .conn-section-h { font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: .07em; color: var(--muted); margin-bottom: 8px; display: flex; align-items: center; gap: 8px; }
        .conn-section-h .msg { font-size: 10px; text-transform: none; letter-spacing: 0; }
        .info { display: inline-flex; align-items: center; justify-content: center; width: 11px; height: 11px; border-radius: 50%; background: var(--panel2); border: 1px solid var(--border2); color: var(--muted); font-size: 8px; font-weight: 700; font-style: italic; cursor: help; margin-left: 3px; user-select: none; vertical-align: middle; }
        .info:hover { color: var(--accent); border-color: var(--accent); }
        .tip { position: fixed; z-index: 9999; background: var(--panel2); color: var(--text); border: 1px solid var(--border2); border-radius: 5px; padding: 7px 11px; font-size: 11px; font-weight: 400; line-height: 1.5; max-width: 280px; box-shadow: 0 6px 16px rgba(0,0,0,.4); pointer-events: none; opacity: 0; transition: opacity .1s ease; }
        .tip.show { opacity: 1; }
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
    </div>
    <div class="clock" id="clock">&#8212;</div>
</div>
<div class="app-shell">
<div class="tabbar">
    <button class="tabbtn active" data-tab="monitor" onclick="showTab('monitor')">Monitor</button>
    <button class="tabbtn" data-tab="connection" onclick="showTab('connection')">Connection</button>
    <button class="tabbtn" data-tab="tags" onclick="showTab('tags')">Tags</button>
    <button class="tabbtn" data-tab="logs" onclick="showTab('logs')">Logs</button>
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
            <div class="mon-stat-group-h">Polling</div>
            <div class="stat"><div class="k">Default Rate</div><div class="v" id="updateRate">&#8212;</div><div class="s" id="mappingCount">0 tags</div></div>
            <div class="stat"><div class="k">Cycle Budget</div><div class="mini-meter" aria-hidden="true"><div class="mini-meter-track"><div class="mini-meter-fill" id="pollUtilizationFill"></div></div></div><div class="s" id="pollUtilizationText">—</div><div class="s" id="pollSaturation">—</div></div>
        </div>
    </div>
    <div class="grid2" style="margin-bottom:14px">
        <div class="box">
            <div class="box-h">Source Status <span class="msg" id="sourceCountH" style="margin-left:auto"></span></div>
            <div class="box-b"><div class="list" id="sourceStatusList" style="max-height:300px"></div></div>
        </div>
        <div class="box">
            <div class="box-h">OPC UA Endpoint</div>
            <div class="box-b"><div class="endpoint" id="uaEndpoint">&#8212;</div><div class="msg" id="uaDiagnostics" style="margin-top:8px">0 nodes · no updates yet</div></div>
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
                        <div class="conn-section-h">Default Poll Rate <span class="info" data-tip="Fallback rate for tags set to 'Source Default' (Tags tab → faceplate → Poll Rate).">i</span></div>
                        <div class="field"><label class="fl">Rate</label><select id="cfgUpdateRate"><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000">1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select><button class="btn ghost" id="cfgApplyRate" type="button">Apply</button><span class="msg" id="rateMessage">Applies live</span></div>
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
        </div>
    </div>
</div>
<div class="view" id="view-tags">
    <div class="box" style="margin-bottom:14px">
        <div class="box-h">Tag Browser</div>
        <div class="box-b">
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
            <div class="field">
                <label class="fl">DA Source</label>
                <select id="mapSourceSelect"></select>
            </div>
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
            <div class="list" id="mappedList"></div>
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
            <div class="fp-body">
                <div class="fp-panel" id="fpLivePanel"></div>
                <div class="fp-panel">
                    <div class="fp-k">Manual override</div>
                    <input class="fp-input" id="fpManualInput" data-action="tag-manual-value" type="text" disabled>
                    <div class="fp-hint" id="fpModeHint"></div>
                </div>
            </div>
        </div>
        <div class="modal-f">
            <div class="field"><label class="fl" style="width:auto">Enabled</label><input type="checkbox" id="fpEnabled" data-action="toggle-tag-enabled"></div>
            <div class="field"><label class="fl" style="width:auto">Mode</label><select id="fpMode" data-action="tag-mode"><option value="Source">Source</option><option value="Manual">Manual</option></select></div>
            <div class="field"><label class="fl" style="width:auto">Poll Rate</label><select id="fpPollRate" data-action="tag-poll-rate"><option value="0">Source Default</option><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000">1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select></div>
            <button class="btn ghost" type="button" id="fpApply" data-action="save-tag">Apply</button>
            <button class="btn ghost" type="button" id="fpRemove" data-action="remove-mapping">Remove</button>
        </div>
    </div>
</div>
<div class="view" id="view-logs">
    <div class="box">
        <div class="box-h">Recent Logs</div>
        <div class="box-b log-panel">
            <div class="toolbar">
                <button class="btn ghost" id="btnRefreshLogs" type="button">Refresh Logs</button>
                <label class="fl" for="logLevel" style="width:auto">Minimum Level</label>
                <select id="logLevel">
                    <option value="Information">Information</option>
                    <option value="Warning">Warning</option>
                    <option value="Error">Error</option>
                </select>
                <span class="msg" id="logMessage">Showing recent in-app logs.</span>
            </div>
            <div class="log-view" id="logEntries"><span class="msg">Loading logs…</span></div>
        </div>
    </div>
</div>
<div class="view" id="view-help">
    <div class="help-accordion" id="helpContent"><span class="msg">Loading help…</span></div>
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
            </div>
        </div>
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
    if (x + tr.width > window.innerWidth - 4) x = window.innerWidth - tr.width - 4;
    tipEl.style.left = x + 'px';
    tipEl.style.top = y + 'px';
});
document.addEventListener('mouseout', e => { if (e.target.closest('.info') && tipEl) tipEl.classList.remove('show'); });
const el = id => document.getElementById(id);
const state = {
    tagPath: '',
    sources: [],
    selectedSourceId: 'default',
    editingNewSource: false,
    liveValuesEnabled: true,
    lastValueCount: 0,
    updateRateMs: 1000,
    logsLoaded: false,
    appInfoLoaded: false,
    mappings: [],
    valuesByKey: new Map()
};

function valueKey(sourceId, itemId) {
    return (sourceId || 'default') + '\u0000' + (itemId || '');
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

function renderMappingRow(mapping) {
    const sourceId = mapping.sourceId || mapping.SourceId || 'default';
    const item = mapping.daItemId || mapping.DaItemId;
    const name = mapping.displayName || mapping.DisplayName || item;
    const node = mapping.uaNodeId || mapping.UaNodeId || defaultUaNodeId(sourceId, item);
    const mode = mapping.mode || mapping.Mode || 'Source';
    const enabled = (mapping.enabled ?? mapping.Enabled) !== false;
    const modeBadge = mode === 'Manual' ? badge('Manual', 'warn') : (enabled ? badge('Source', 'good') : badge('Disabled', 'bad'));
    const pollRate = mapping.pollRateMs ?? mapping.PollRateMs ?? 0;
    const rateBadge = pollRate > 0 ? `<span class="pill" style="padding:1px 6px;font-size:10px">${pollRate}ms</span>` : '';
    return `<div class="li clickable" data-action="open-faceplate" data-source-id="${attr(sourceId)}" data-item-id="${attr(item)}"><div style="flex:1"><div class="n">${esc(name)}</div><div class="p">${esc(sourceId)} · ${esc(item)} · UA: ${esc(node)}</div></div><div class="li-badge">${rateBadge}${modeBadge}</div></div>`;
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
    const enabled = (mapping.enabled ?? mapping.Enabled) !== false;
    const manualValue = mapping.manualValue ?? mapping.ManualValue ?? '';
    el('fpName').textContent = name;
    el('fpSub').textContent = sourceId + ' · ' + itemId + ' · UA: ' + node;
    el('fpEnabled').checked = enabled;
    el('fpMode').value = mode;
    el('fpManualInput').value = String(manualValue ?? '');
    el('fpManualInput').disabled = mode !== 'Manual';
    const pollRate = mapping.pollRateMs ?? mapping.PollRateMs ?? 0;
    el('fpPollRate').value = String(pollRate);
    el('fpModeHint').textContent = 'Mode ' + mode;
    el('fpApply').dataset.sourceId = sourceId;
    el('fpApply').dataset.itemId = itemId;
    el('fpRemove').dataset.sourceId = sourceId;
    el('fpRemove').dataset.itemId = itemId;
    el('fpEnabled').dataset.sourceId = sourceId;
    el('fpEnabled').dataset.itemId = itemId;
    el('fpLivePanel').innerHTML = renderLiveValue(currentValue(sourceId, itemId));
    el('faceplateOverlay').classList.add('open');
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
    const modeSelect = el('fpMode');
    const manualInput = el('fpManualInput');
    if (!modeSelect || !manualInput) return;
    manualInput.disabled = modeSelect.value !== 'Manual';
    el('fpModeHint').textContent = 'Mode ' + modeSelect.value;
}

function showTab(name) {
    document.querySelectorAll('.tabbtn').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
    document.querySelectorAll('.view').forEach(v => v.classList.toggle('active', v.id === 'view-' + name));
    if (location.hash !== '#' + name) history.replaceState(null, '', '#' + name);
    if (name === 'logs') loadLogs().catch(e => el('logMessage').textContent = '✗ ' + e.message);
    if (name === 'about') loadAppInfo().catch(e => el('aboutName').textContent = '✗ ' + e.message);
    if (name === 'help') loadHelp().catch(e => el('helpContent').innerHTML = '<span class="msg bad">✗ ' + esc(e.message) + '</span>');
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
    if (document.activeElement !== el('cfgUpdateRate')) el('cfgUpdateRate').value = String(state.updateRateMs);
    renderSources();
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
    if (duration >= rate) return { text: 'Poll saturated · cycle time is at or above the configured rate.', className: 's bad' };
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
    el('logMessage').textContent = 'Loading logs…';
    const payload = await (await fetch('/api/logs?limit=200&level=' + encodeURIComponent(level), { cache: 'no-store' })).json();
    const entries = payload.entries || [];
    el('logEntries').innerHTML = entries.length ? entries.map(entry => {
        const timestamp = locTime(entry.timestampUtc || entry.TimestampUtc);
        const levelText = entry.level || entry.Level || 'Information';
        const category = entry.category || entry.Category || 'App';
        const message = entry.message || entry.Message || '';
        const exceptionText = entry.exceptionText || entry.ExceptionText || '';
        return `<div class="log-entry"><div class="meta">${esc(timestamp)} · ${esc(levelText)} · ${esc(category)}</div><div class="message">${esc(message)}</div>${exceptionText ? `<div class="exception">${esc(exceptionText)}</div>` : ''}</div>`;
    }).join('') : '<span class="msg">No log entries for this filter yet.</span>';
    el('logMessage').textContent = entries.length + ' recent entries';
    state.logsLoaded = true;
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
    const sections = (p.markdown || '').split(/\r?\n---\r?\n/).filter(s => s.trim());
    const container = el('helpContent');
    container.innerHTML = sections.map((section, i) => {
        const titleMatch = section.match(/^#\s+(.+)/m);
        const title = titleMatch ? titleMatch[1] : 'Section';
        const body = renderMarkdown(section.replace(/^#\s+.+/m, ''));
        const openAttr = i < 2 ? ' open' : '';
        return `<details class="help-section"${openAttr}><summary>${esc(title)}</summary><div class="help-body">${body}</div></details>`;
    }).join('');
    helpLoaded = true;
}

async function refresh() {
    try {
        const p = await (await fetch('/api/dashboard', { cache: 'no-store' })).json();
        const b = p.bridge || p.Bridge || {};
        const ua = p.ua || p.Ua || {};
        const vs = p.values || p.Values || [];
        const sources = get(b, 'sources') || [];
        el('dot').className = 'dot';
        el('clock').textContent = new Date().toLocaleTimeString();
        el('pBridge').innerHTML = badge(get(b, 'bridgeState') || '—', stateClass(get(b, 'bridgeState')));
        el('pDa').innerHTML = badge(get(b, 'daConnectionState') || '—', stateClass(get(b, 'daConnectionState')));
        el('pUa').innerHTML = badge(get(ua, 'state') || '—', stateClass(get(ua, 'state')));
        el('pTags').textContent = get(b, 'mappingCount') ?? 0;
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
        el('updateRate').textContent = updateRateMs + ' ms';
        el('pollUtilizationFill').style.width = pollUtilization.width;
        el('pollUtilizationFill').className = pollUtilization.className;
        el('pollUtilizationText').textContent = pollUtilization.text;
        el('pollSaturation').textContent = pollSaturation.text;
        el('pollSaturation').className = pollSaturation.className;
        if (document.activeElement !== el('cfgUpdateRate')) el('cfgUpdateRate').value = String(updateRateMs);
        el('mappingCount').textContent = (get(b, 'mappingCount') ?? 0) + ' tags';
        el('uaEndpoint').textContent = get(ua, 'endpointUrl') || '—';
        el('uaDiagnostics').textContent = formatUaDiagnostics(ua);
        const srcCountH = el('sourceCountH'); if (srcCountH) srcCountH.textContent = sources.length + ' source' + (sources.length !== 1 ? 's' : '');
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
async function loadMappings() {
    const p = await (await fetch('/api/mappings', { cache: 'no-store' })).json();
    const mappings = p.mappings || [];
    state.mappings = mappings;
    el('mapCount').textContent = mappings.length + ' mappings';
    el('mappedList').innerHTML = renderMappingRows(mappings);
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
        dataType: mapping.dataType || mapping.DataType || 'Auto',
        uaNodeId: mapping.uaNodeId || mapping.UaNodeId || defaultUaNodeId(sourceId, itemId),
        enabled: (mapping.enabled ?? mapping.Enabled) !== false,
        mode: mapping.mode || mapping.Mode || 'Source',
        manualValue: mapping.manualValue ?? mapping.ManualValue ?? null,
        pollRateMs: mapping.pollRateMs ?? mapping.PollRateMs ?? 0
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
    renderSources();
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
                payload.mode = el('fpMode').value || 'Source';
                payload.pollRateMs = Number.parseInt(el('fpPollRate').value, 10) || 0;
                if (payload.mode === 'Manual') {
                    const manualField = el('fpManualInput');
                    if (!manualField.value.trim()) {
                        const liveText = el('fpLivePanel')?.querySelector('.fp-v')?.textContent || '';
                        manualField.value = liveText;
                    }
                    payload.manualValue = manualField.value.trim() || '';
                    payload.enabled = true;
                } else {
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
                if (!target.checked) { payload.mode = 'Source'; payload.manualValue = null; }
            }).then(() => openFaceplate(target.dataset.sourceId || '', target.dataset.itemId || '')).catch(e => alert('Update failed: ' + e.message));
            return;
        }
        if (target.id === 'fpMode') {
            updateManualInputState();
        }
    });
}

document.addEventListener('DOMContentLoaded', async () => {
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
    el('btnReloadServers').addEventListener('click', () => browseServers().catch(e => el('msgServers').textContent = e.message));
    el('btnBrowseTags').addEventListener('click', () => browseTags('').catch(e => el('tagTree').innerHTML = `<span class="bad">${esc(e.message)}</span>`));
    el('btnBrowseAllTags').addEventListener('click', () => browseTags('', true).catch(e => el('tagTree').innerHTML = `<span class="bad">${esc(e.message)}</span>`));
    el('manualAdd').addEventListener('click', () => addManual().catch(e => alert('Add failed: ' + e.message)));
    el('toggleLiveValues').addEventListener('click', toggleLiveValues);
    el('btnRefreshLogs').addEventListener('click', () => loadLogs(true).catch(e => el('logMessage').textContent = '✗ ' + e.message));
    el('logLevel').addEventListener('change', () => {
        state.logsLoaded = false;
        loadLogs(true).catch(e => el('logMessage').textContent = '✗ ' + e.message);
    });
    bindDynamicButtons();
    const initTab = location.hash.slice(1);
    if (['monitor','connection','tags','logs','help','about'].includes(initTab)) showTab(initTab);
    await loadSources();
    await loadMappings();
    updateLiveValuesUi();
    await refresh();
    if (initTab === 'logs') await loadLogs();
    if (initTab === 'help') await loadHelp();
    if (initTab === 'about') await loadAppInfo();
    fetch('/api/version').then(r => r.json()).then(p => { const v = (p.informationalVersion || p.version || '0.0.0').split('+')[0]; el('appVersion').textContent = 'v' + v; }).catch(() => {});
    setInterval(refresh, 1000);
});
</script>
</body>
</html>
""";

    public static string FullHtml => Html + Script;
}
