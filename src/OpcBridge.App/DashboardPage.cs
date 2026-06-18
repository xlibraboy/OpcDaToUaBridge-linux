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
            --bg:       #0a0e14;
            --panel:    #11161f;
            --panel2:   #161c27;
            --border:   #232b38;
            --border2:  #2e3848;
            --text:     #d8e0ea;
            --muted:    #6b7689;
            --good:     #34d399;
            --bad:      #f87171;
            --warn:     #fbbf24;
            --accent:   #38bdf8;
            font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { background: var(--bg); color: var(--text); font-size: 13px; }
        .mono { font-family: 'Consolas', 'SF Mono', monospace; }

        /* ── Top bar ─────────────────────────────── */
        .topbar {
            display: flex; align-items: center; gap: 14px;
            padding: 0 18px; height: 46px;
            background: var(--panel); border-bottom: 1px solid var(--border2);
        }
        .brand { display: flex; align-items: center; gap: 9px; font-weight: 600; font-size: 14px; white-space: nowrap; }
        .dot { width: 9px; height: 9px; border-radius: 50%; background: var(--good); box-shadow: 0 0 0 0 rgba(52,211,153,.5); animation: pulse 2s infinite; }
        .dot.off { background: var(--bad); animation: none; box-shadow: none; }
        @keyframes pulse { 0%{box-shadow:0 0 0 0 rgba(52,211,153,.5)} 70%{box-shadow:0 0 0 6px rgba(52,211,153,0)} 100%{box-shadow:0 0 0 0 rgba(52,211,153,0)} }

        /* status pills in top bar */
        .pills { display: flex; gap: 7px; margin-left: 8px; flex-wrap: wrap; }
        .pill {
            display: flex; align-items: center; gap: 6px;
            background: var(--panel2); border: 1px solid var(--border);
            border-radius: 5px; padding: 3px 9px; font-size: 12px; white-space: nowrap;
        }
        .pill b { font-weight: 600; }
        .pill .k { color: var(--muted); text-transform: uppercase; font-size: 10px; letter-spacing: .05em; }
        .topbar .clock { margin-left: auto; color: var(--muted); font-size: 11px; white-space: nowrap; }

        /* ── Tabs ─────────────────────────────────── */
        .tabbar { display: flex; background: var(--panel); border-bottom: 1px solid var(--border2); padding: 0 12px; }
        .tabbtn {
            background: none; border: none; color: var(--muted);
            padding: 11px 18px; font-size: 13px; font-weight: 500; cursor: pointer;
            border-bottom: 2px solid transparent; display: flex; align-items: center; gap: 7px;
        }
        .tabbtn:hover { color: var(--text); }
        .tabbtn.active { color: var(--accent); border-bottom-color: var(--accent); }

        /* ── Layout ───────────────────────────────── */
        .view { display: none; padding: 16px 18px; }
        .view.active { display: block; }
        .grid2 { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
        @media (max-width: 900px) { .grid2 { grid-template-columns: 1fr; } }

        /* ── Card / panel ─────────────────────────── */
        .box { background: var(--panel); border: 1px solid var(--border); border-radius: 7px; overflow: hidden; }
        .box-h {
            padding: 9px 14px; background: var(--panel2); border-bottom: 1px solid var(--border);
            font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: .07em; color: var(--muted);
            display: flex; align-items: center; gap: 8px;
        }
        .box-b { padding: 12px 14px; }

        /* stat tiles */
        .stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 10px; margin-bottom: 14px; }
        .stat { background: var(--panel); border: 1px solid var(--border); border-radius: 7px; padding: 11px 13px; }
        .stat .k { color: var(--muted); font-size: 10px; text-transform: uppercase; letter-spacing: .06em; }
        .stat .v { margin-top: 6px; font-size: 16px; font-weight: 700; line-height: 1.1; }
        .stat .s { margin-top: 4px; color: var(--muted); font-size: 11px; }

        /* badges */
        .badge { display: inline-flex; align-items: center; gap: 5px; padding: 1px 8px; border-radius: 10px; font-size: 12px; font-weight: 600; }
        .badge::before { content:''; width:6px; height:6px; border-radius:50%; background:currentColor; }
        .badge.good { color: var(--good); background: rgba(52,211,153,.12); }
        .badge.bad  { color: var(--bad);  background: rgba(248,113,113,.12); }
        .badge.warn { color: var(--warn); background: rgba(251,191,36,.12); }

        /* tables */
        table { width: 100%; border-collapse: collapse; }
        th { background: var(--panel2); color: var(--muted); font-size: 10px; text-transform: uppercase; letter-spacing: .06em; font-weight: 600; padding: 8px 12px; text-align: left; border-bottom: 1px solid var(--border); }
        td { padding: 8px 12px; border-bottom: 1px solid var(--border); }
        tr:last-child td { border-bottom: none; }
        tbody tr:hover td { background: var(--panel2); }

        .good { color: var(--good); } .bad { color: var(--bad); } .warn { color: var(--warn); }
        code { color: var(--accent); font-family: 'Consolas', monospace; font-size: 12px; }

        /* form controls */
        .field { display: flex; align-items: center; gap: 8px; margin-bottom: 10px; flex-wrap: wrap; }
        .field:last-child { margin-bottom: 0; }
        label.fl { color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: .05em; width: 86px; flex-shrink: 0; }
        select, input[type=text], input[type=password] {
            background: var(--bg); color: var(--text); border: 1px solid var(--border2);
            border-radius: 5px; padding: 6px 9px; font-size: 13px;
        }
        select:focus, input:focus { outline: none; border-color: var(--accent); }
        input[type=text], input[type=password] { min-width: 160px; }
        .btn {
            display: inline-flex; align-items: center; gap: 6px;
            background: var(--accent); color: #07121a; border: none; border-radius: 5px;
            padding: 6px 13px; font-size: 12px; font-weight: 600; cursor: pointer; white-space: nowrap;
        }
        .btn:hover:not(:disabled) { filter: brightness(1.1); }
        .btn:disabled { opacity: .4; cursor: not-allowed; }
        .btn.ghost { background: var(--panel2); color: var(--text); border: 1px solid var(--border2); }
        .btn.ghost:hover:not(:disabled) { border-color: var(--accent); color: var(--accent); filter: none; }
        .hint { font-size: 12px; color: var(--muted); }
        .endpoint { background: var(--bg); border: 1px solid var(--border2); border-radius: 5px; padding: 7px 11px; font-family: 'Consolas', monospace; font-size: 12px; color: var(--accent); word-break: break-all; }

        /* sub-tabs (local/remote) */
        .subtabs { display: flex; gap: 4px; }
        .subtab { background: var(--bg); border: 1px solid var(--border2); color: var(--muted); padding: 4px 12px; font-size: 12px; border-radius: 5px; cursor: pointer; }
        .subtab.active { background: var(--accent); color: #07121a; border-color: var(--accent); font-weight: 600; }

        /* lists */
        .list { display: flex; flex-direction: column; gap: 3px; max-height: 260px; overflow-y: auto; }
        .li { display: flex; flex-direction: column; padding: 8px 10px; border-radius: 5px; cursor: pointer; border: 1px solid transparent; }
        .li:hover { background: var(--panel2); border-color: var(--border); }
        .li.sel { background: rgba(56,189,248,.08); border-color: var(--accent); }
        .li .n { font-size: 13px; font-weight: 600; }
        .li .p { font-size: 11px; color: var(--muted); font-family: 'Consolas', monospace; margin-top: 2px; }

        /* tag tree */
        .tree { max-height: 420px; overflow-y: auto; }
        .tn { display: flex; align-items: center; gap: 8px; padding: 6px 9px; border-radius: 5px; cursor: pointer; font-size: 13px; }
        .tn:hover { background: var(--panel2); }
        .tn.b .ic { color: var(--warn); }
        .tn.t .ic { color: var(--accent); }
        .tn .ic { color: var(--muted); }
        .tn .pid { color: var(--muted); font-family: 'Consolas', monospace; font-size: 11px; margin-left: auto; }
        .crumb { color: var(--accent); cursor: pointer; }
        .crumb:hover { text-decoration: underline; }
        .icon { width: 14px; height: 14px; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; flex-shrink: 0; }
        .msg { font-size: 12px; color: var(--muted); }
    </style>
</head>
<body>

<!-- Top status bar -->
<div class="topbar">
    <div class="brand"><span class="dot" id="dot"></span>OPC DA&#8594;UA Bridge</div>
    <div class="pills">
        <div class="pill"><span class="k">Bridge</span><span id="pBridge">&#8212;</span></div>
        <div class="pill"><span class="k">DA</span><span id="pDa">&#8212;</span></div>
        <div class="pill"><span class="k">UA</span><span id="pUa">&#8212;</span></div>
        <div class="pill"><span class="k">Mode</span><b id="pMode">&#8212;</b></div>
        <div class="pill"><span class="k">Tags</span><b id="pTags">0</b></div>
        <div class="pill"><span class="k">Clients</span><b id="pClients">0</b></div>
    </div>
    <div class="clock" id="clock">&#8212;</div>
</div>

<!-- Tabs -->
<div class="tabbar">
    <button class="tabbtn active" data-tab="monitor" onclick="showTab('monitor')">
        <svg class="icon" viewBox="0 0 24 24"><path d="M3 3v18h18"/><path d="M18 17V9M13 17V5M8 17v-3"/></svg> Monitor
    </button>
    <button class="tabbtn" data-tab="connection" onclick="showTab('connection')">
        <svg class="icon" viewBox="0 0 24 24"><path d="M5 12.55a11 11 0 0 1 14.08 0M1.42 9a16 16 0 0 1 21.16 0M8.53 16.11a6 6 0 0 1 6.95 0M12 20h.01"/></svg> Connection
    </button>
    <button class="tabbtn" data-tab="tags" onclick="showTab('tags')">
        <svg class="icon" viewBox="0 0 24 24"><path d="M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z"/><line x1="7" y1="7" x2="7.01" y2="7"/></svg> Tags
    </button>
</div>

<!-- ═══ MONITOR ═══ -->
<div class="view active" id="view-monitor">
    <div class="stats">
        <div class="stat"><div class="k">Bridge runtime</div><div class="v" id="bridgeState">&#8212;</div><div class="s" id="lastError">No errors</div></div>
        <div class="stat"><div class="k">OPC DA client</div><div class="v" id="daState">&#8212;</div><div class="s" id="daMode">Mode: &#8212;</div></div>
        <div class="stat"><div class="k">Last DA read</div><div class="v" id="lastDaRead">&#8212;</div><div class="s" id="lastDaReadCount">0 values</div></div>
        <div class="stat"><div class="k">Last UA write</div><div class="v" id="lastUaWrite">&#8212;</div><div class="s" id="lastUaWriteCount">0 values</div></div>
        <div class="stat"><div class="k">OPC UA server</div><div class="v" id="uaState">&#8212;</div><div class="s" id="uaClients">0 clients</div></div>
        <div class="stat"><div class="k">Update rate</div><div class="v" id="updateRate">&#8212;</div><div class="s" id="mappingCount">0 tags</div></div>
    </div>
    <div class="box">
        <div class="box-h">Live Values &#8212; OPC DA &#8594; Bridge <span class="msg" id="valCount" style="margin-left:auto;text-transform:none;letter-spacing:0"></span></div>
        <table>
            <thead><tr><th>DA Item ID</th><th>Value</th><th>Quality</th><th>Timestamp (local)</th></tr></thead>
            <tbody id="values"><tr><td colspan="4" class="msg">Waiting for values&#8230;</td></tr></tbody>
        </table>
    </div>
</div>

<!-- ═══ CONNECTION ═══ -->
<div class="view" id="view-connection">
    <div class="grid2">
        <!-- Left column -->
        <div>
            <div class="box" style="margin-bottom:14px">
                <div class="box-h">Bridge Mode</div>
                <div class="box-b">
                    <div class="field">
                        <label class="fl">Source</label>
                        <select id="modeSelect">
                            <option value="Simulation">Simulation</option>
                            <option value="OpcDa">Real OPC DA</option>
                        </select>
                        <button class="btn" id="modeApply" type="button">
                            <svg class="icon" viewBox="0 0 24 24"><polyline points="20 6 9 17 4 12"/></svg> Apply
                        </button>
                    </div>
                    <div class="hint" id="modeMessage">No pending change</div>
                </div>
            </div>
            <div class="box">
                <div class="box-h">OPC UA Endpoint</div>
                <div class="box-b"><div class="endpoint" id="uaEndpoint">&#8212;</div></div>
            </div>
        </div>

        <!-- Right column: server browser -->
        <div class="box">
            <div class="box-h">
                OPC DA Server Browser
                <span class="subtabs" style="margin-left:auto">
                    <span class="subtab active" id="stLocal"  onclick="switchSrvTab('local')">Local</span>
                    <span class="subtab" id="stRemote" onclick="switchSrvTab('remote')">Remote</span>
                </span>
            </div>
            <div class="box-b">
                <div id="paneLocal">
                    <div class="field">
                        <button class="btn ghost" id="btnReloadLocal" type="button" onclick="browseServers(null)">
                            <svg class="icon" viewBox="0 0 24 24"><polyline points="23 4 23 10 17 10"/><polyline points="1 20 1 14 7 14"/><path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/></svg> Reload
                        </button>
                        <span class="msg" id="msgLocal">Scan local servers.</span>
                    </div>
                    <div class="list" id="listLocal"></div>
                </div>
                <div id="paneRemote" style="display:none">
                    <div class="field">
                        <input id="remoteHost" type="text" placeholder="192.168.x.x" style="width:150px">
                        <button class="btn ghost" id="btnReloadRemote" type="button" onclick="browseServers(el('remoteHost').value)">
                            <svg class="icon" viewBox="0 0 24 24"><polyline points="23 4 23 10 17 10"/><polyline points="1 20 1 14 7 14"/><path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/></svg> Browse
                        </button>
                        <span class="msg" id="msgRemote">Enter host &amp; browse.</span>
                    </div>
                    <div class="list" id="listRemote"></div>
                </div>
            </div>
            <div class="box-h" style="border-top:1px solid var(--border);border-bottom:none">Selected Server</div>
            <div class="box-b">
                <div class="field"><label class="fl">ProgID</label><input id="cfgProgId" type="text" placeholder="ProgID" style="flex:1"></div>
                <div class="field"><label class="fl">Host</label><input id="cfgHost" type="text" placeholder="localhost" style="flex:1" oninput="toggleCreds()"></div>
                <div class="field" id="credRow" style="display:none"><label class="fl">User</label><input id="cfgUser" type="text" placeholder="username" style="width:130px"><input id="cfgPass" type="password" placeholder="password" style="width:120px"><input id="cfgDomain" type="text" placeholder="domain" style="width:110px"></div>
                <div class="field">
                    <button class="btn" id="cfgApply" type="button" onclick="applyServerConfig()">
                        <svg class="icon" viewBox="0 0 24 24"><polyline points="20 6 9 17 4 12"/></svg> Apply &amp; Connect
                    </button>
                    <span class="msg" id="cfgMessage">&#8212;</span>
                </div>
            </div>
        </div>
    </div>
</div>

<!-- ═══ TAGS ═══ -->
<div class="view" id="view-tags">
    <div class="grid2">
        <!-- Tag browser -->
        <div class="box">
            <div class="box-h">
                Tag Browser
                <button class="btn ghost" id="btnBrowseTags" type="button" onclick="browseTags('')" style="margin-left:auto">
                    <svg class="icon" viewBox="0 0 24 24"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg> Browse
                </button>
            </div>
            <div class="box-b" style="border-bottom:1px solid var(--border);padding:9px 14px">
                <span class="msg" id="tagBreadcrumb">Uses the Selected server (Connection tab). Click + to map a tag.</span>
            </div>
            <div class="box-b"><div class="tree" id="tagTree"><span class="msg">Click "Browse" to explore the server's address space.</span></div></div>
        </div>

        <!-- Mapped tags -->
        <div class="box">
            <div class="box-h">
                Mapped Tags <span class="msg" id="mapCount" style="margin-left:auto;text-transform:none;letter-spacing:0"></span>
            </div>
            <div class="box-b">
                <div class="field" style="margin-bottom:8px">
                    <input id="manualItem" type="text" placeholder="Manual DA Item ID" style="flex:1">
                    <button class="btn ghost" type="button" onclick="addManual()">
                        <svg class="icon" viewBox="0 0 24 24"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg> Add
                    </button>
                </div>
                <div class="list" id="mappedList" style="max-height:380px"></div>
            </div>
        </div>
    </div>
</div>
""";

    public const string Script = """
<script>
const ESC = {'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'};
const esc = s => String(s??'').replace(/[&<>'"]/g, c => ESC[c]);
const el  = id => document.getElementById(id);
let modeInFlight = false, pendingMode = null, tagPath = '';

// ── Tabs ──────────────────────────────────────────────────────────
function showTab(name) {
    document.querySelectorAll('.tabbtn').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
    document.querySelectorAll('.view').forEach(v => v.classList.toggle('active', v.id === 'view-' + name));
    if (location.hash !== '#' + name) history.replaceState(null, '', '#' + name);
}
function switchSrvTab(t) {
    const loc = t === 'local';
    el('stLocal').classList.toggle('active', loc);
    el('stRemote').classList.toggle('active', !loc);
    el('paneLocal').style.display  = loc ? '' : 'none';
    el('paneRemote').style.display = loc ? 'none' : '';
}

// ── Helpers ───────────────────────────────────────────────────────
function badge(t, c) { return `<span class="badge ${c}">${esc(t)}</span>`; }
function stateClass(v) {
    if (!v) return 'warn';
    const s = v.toLowerCase();
    if (s === 'running' || s === 'connected') return 'good';
    if (s === 'faulted' || s === 'stopped' || s === 'disconnected') return 'bad';
    return 'warn';
}
function relTime(u) {
    if (!u) return '\u2014';
    const d = Math.floor((Date.now() - new Date(u)) / 1000);
    if (d < 5) return 'just now';
    if (d < 60) return d + 's ago';
    if (d < 3600) return Math.floor(d/60) + 'm ago';
    return new Date(u).toLocaleTimeString();
}
function locTime(u) { return u ? new Date(u).toLocaleString() : '\u2014'; }
function get(o, k) { return o[k] ?? o[k[0].toUpperCase() + k.slice(1)]; }

// ── Dashboard refresh ─────────────────────────────────────────────
async function refresh() {
    try {
        const r = await fetch('/api/dashboard', { cache: 'no-store' });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const p = await r.json();
        const b = p.bridge||p.Bridge||{}, ua = p.ua||p.Ua||{}, vs = p.values||p.Values||[];

        el('dot').className = 'dot';
        el('clock').textContent = new Date().toLocaleTimeString();

        // top pills
        el('pBridge').innerHTML = badge(get(b,'bridgeState')||'\u2014', stateClass(get(b,'bridgeState')));
        el('pDa').innerHTML     = badge(get(b,'daConnectionState')||'\u2014', stateClass(get(b,'daConnectionState')));
        el('pUa').innerHTML     = badge(get(ua,'state')||'\u2014', stateClass(get(ua,'state')));
        el('pMode').textContent    = get(b,'daMode')||'\u2014';
        el('pTags').textContent    = get(b,'mappingCount')??0;
        el('pClients').textContent = get(ua,'connectedClientCount')??0;

        // monitor stats
        el('bridgeState').innerHTML = badge(get(b,'bridgeState')||'\u2014', stateClass(get(b,'bridgeState')));
        const err = get(b,'lastError');
        el('lastError').textContent = err || 'No errors';
        el('lastError').className = 's' + (err ? ' bad' : '');
        el('daState').innerHTML = badge(get(b,'daConnectionState')||'\u2014', stateClass(get(b,'daConnectionState')));
        el('daMode').textContent = 'Mode: ' + (get(b,'daMode')||'\u2014');
        el('lastDaRead').textContent = relTime(get(b,'lastDaReadUtc'));
        el('lastDaReadCount').textContent = (get(b,'lastDaReadCount')??0) + ' values';
        el('lastUaWrite').textContent = relTime(get(b,'lastUaWriteUtc'));
        el('lastUaWriteCount').textContent = (get(b,'lastUaWriteCount')??0) + ' values';
        el('uaState').innerHTML = badge(get(ua,'state')||'\u2014', stateClass(get(ua,'state')));
        el('uaClients').textContent = (get(ua,'connectedClientCount')??0) + ' clients';
        el('updateRate').textContent = (get(b,'updateRateMs')||'\u2014') + ' ms';
        el('mappingCount').textContent = (get(b,'mappingCount')??0) + ' tags';
        el('uaEndpoint').textContent = get(ua,'endpointUrl') || '\u2014';

        const currentMode = get(b,'daMode') || 'Simulation';
        if (pendingMode && currentMode === pendingMode) pendingMode = null;
        if (!modeInFlight) el('modeSelect').value = pendingMode || currentMode;
        el('values').innerHTML = vs.length ? vs.map(it => {
            const g = get(it,'isGood'), q = get(it,'daQuality');
            return `<tr><td><code>${esc(get(it,'daItemId'))}</code></td>
                <td class="mono">${esc(String(get(it,'value')??''))}</td>
                <td>${badge(g?'Good':'Bad', g?'good':'bad')} <span class="${g?'good':'bad'}" style="font-size:11px;opacity:.6">(${q})</span></td>
                <td class="msg">${esc(locTime(get(it,'timestampUtc')))}</td></tr>`;
        }).join('') : '<tr><td colspan="4" class="msg">No values yet.</td></tr>';
    } catch(e) {
        el('dot').className = 'dot off';
        el('clock').textContent = 'offline';
        el('values').innerHTML = `<tr><td colspan="4" class="bad">${esc(e.message)}</td></tr>`;
    }
}

// ── Mode ──────────────────────────────────────────────────────────
async function applyMode() {
    const sel = el('modeSelect'), btn = el('modeApply'), msg = el('modeMessage');
    const requestedMode = sel.value;
    modeInFlight = true; sel.disabled = btn.disabled = true;
    msg.textContent = 'Applying…'; msg.className = 'hint warn';
    try {
        const r = await fetch('/api/da/mode', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ mode: requestedMode }) });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const p = await r.json();
        pendingMode = p.mode || requestedMode;
        msg.textContent = '✓ Mode set to ' + pendingMode; msg.className = 'hint good';
        await refresh();
    } catch(e) {
        pendingMode = null;
        msg.textContent = '✗ ' + e.message; msg.className = 'hint bad';
    } finally { modeInFlight = false; sel.disabled = btn.disabled = false; }
}

// ── Server browser ────────────────────────────────────────────────
async function browseServers(host) {
    const loc = !host || !host.trim();
    const list = el(loc?'listLocal':'listRemote'), msg = el(loc?'msgLocal':'msgRemote'), btn = el(loc?'btnReloadLocal':'btnReloadRemote');
    const dhost = loc ? 'localhost' : host.trim();
    btn.disabled = true; msg.textContent = 'Scanning\u2026'; msg.className = 'msg warn';
    list.innerHTML = '<span class="msg">Scanning\u2026</span>';
    try {
        const url = '/api/da/servers' + (loc ? '' : '?host=' + encodeURIComponent(dhost));
        const p = await (await fetch(url, { cache:'no-store' })).json();
        if (p.error) throw new Error(p.error);
        const sv = p.servers || [];
        if (!sv.length) { list.innerHTML = '<span class="msg">No servers found.</span>'; msg.textContent='0 found'; msg.className='msg'; return; }
        msg.textContent = sv.length + (sv.length>1?' servers':' server'); msg.className = 'msg good';
        list.innerHTML = sv.map((s,i) => {
            const id = `sv-${loc?'l':'r'}-${i}`, name = s.description||s.Description||s.progId||s.ProgId, prog = s.progId||s.ProgId;
            return `<div class="li" id="${id}" onclick="pickServer(${JSON.stringify(prog)},${JSON.stringify(dhost)},'${id}')"><span class="n">${esc(name)}</span><span class="p">${esc(prog)}</span></div>`;
        }).join('');
    } catch(e) { list.innerHTML = `<span class="bad">${esc(e.message)}</span>`; msg.textContent='\u2717 '+e.message; msg.className='msg bad'; }
    finally { btn.disabled = false; }
}
function pickServer(prog, host, id) {
    document.querySelectorAll('.li.sel').forEach(n => n.classList.remove('sel'));
    el(id)?.classList.add('sel');
    el('cfgProgId').value = prog; el('cfgHost').value = host; toggleCreds();
    el('cfgMessage').textContent = 'Selected \u2014 click Apply & Connect'; el('cfgMessage').className = 'msg warn';
}

// ── Credentials ───────────────────────────────────────────────────
function toggleCreds() {
    const h = el('cfgHost').value.trim().toLowerCase();
    el('credRow').style.display = (h && h !== 'localhost') ? '' : 'none';
}
async function loadServerConfig() {
    try {
        const p = await (await fetch('/api/da/config', { cache:'no-store' })).json();
        el('cfgProgId').value = p.progId || p.ProgId || '';
        el('cfgHost').value   = p.host   || p.Host   || 'localhost';
        el('cfgUser').value   = p.remoteUsername || '';
        el('cfgDomain').value = p.remoteDomain   || '';
        toggleCreds();
    } catch {}
}
async function applyServerConfig() {
    const btn = el('cfgApply'), msg = el('cfgMessage');
    btn.disabled = true; msg.textContent = 'Saving\u2026'; msg.className = 'msg warn';
    try {
        const body = { progId: el('cfgProgId').value.trim(), host: el('cfgHost').value.trim()||'localhost',
            remoteUsername: el('cfgUser').value.trim()||null, remotePassword: el('cfgPass').value||null, remoteDomain: el('cfgDomain').value.trim()||null };
        const r = await fetch('/api/da/config', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        // Also switch to OpcDa mode automatically
        await fetch('/api/da/mode', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ mode: 'OpcDa' }) });
        pendingMode = 'OpcDa';
        el('modeSelect').value = 'OpcDa';
        msg.textContent = '\u2713 Server saved \u2014 switching to OpcDa mode\u2026'; msg.className = 'msg good';
        await refresh();
    } catch(e) { msg.textContent = '\u2717 ' + e.message; msg.className = 'msg bad'; }
    finally { btn.disabled = false; }
}

// ── Tag browser ───────────────────────────────────────────────────
function renderCrumb() {
    const parts = tagPath ? tagPath.split('.') : [];
    let html = `<span class="crumb" onclick="browseTags('')">root</span>`, acc = '';
    for (const part of parts) { acc = acc ? acc+'.'+part : part; html += ` / <span class="crumb" onclick="browseTags(${JSON.stringify(acc)})">${esc(part)}</span>`; }
    el('tagBreadcrumb').innerHTML = html;
}
async function browseTags(path) {
    const btn = el('btnBrowseTags'), tree = el('tagTree');
    const progId = el('cfgProgId').value.trim(), host = el('cfgHost').value.trim()||'localhost';
    if (!progId) { tree.innerHTML = '<span class="bad">Select a server in the Connection tab first.</span>'; return; }
    tagPath = path || ''; btn.disabled = true;
    tree.innerHTML = '<span class="msg">Browsing\u2026</span>';
    try {
        const body = { progId, host, path: tagPath, remoteUsername: el('cfgUser').value.trim()||null, remotePassword: el('cfgPass').value||null, remoteDomain: el('cfgDomain').value.trim()||null };
        const p = await (await fetch('/api/da/tags', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) })).json();
        if (p.error) throw new Error(p.error);
        renderCrumb();
        const br = p.branches||[], tg = p.tags||[];
        let html = '';
        if (tagPath) { const parent = tagPath.includes('.') ? tagPath.slice(0,tagPath.lastIndexOf('.')) : '';
            html += `<div class="tn" onclick="browseTags(${JSON.stringify(parent)})"><svg class="icon" viewBox="0 0 24 24"><polyline points="15 18 9 12 15 6"/></svg><span class="msg">.. (up)</span></div>`; }
        for (const b of br) { const child = tagPath ? tagPath+'.'+b : b;
            html += `<div class="tn b" onclick="browseTags(${JSON.stringify(child)})"><svg class="icon" viewBox="0 0 24 24"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg><span>${esc(b)}</span></div>`; }
        for (const t of tg) { const name = t.name||t.Name, itemId = t.itemId||t.ItemId;
            html += `<div class="tn t"><svg class="icon" viewBox="0 0 24 24"><path d="M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z"/><line x1="7" y1="7" x2="7.01" y2="7"/></svg><span>${esc(name)}</span><span class="pid">${esc(itemId)}</span><button class="btn ghost" style="padding:2px 8px;margin-left:8px" onclick="addTag(${JSON.stringify(itemId)},${JSON.stringify(name)})">+ Add</button></div>`; }
        if (!br.length && !tg.length) html += '<span class="msg">Empty.</span>';
        tree.innerHTML = html;
    } catch(e) { tree.innerHTML = `<span class="bad">${esc(e.message)}</span>`; }
    finally { btn.disabled = false; }
}
// ── Mapped tags ───────────────────────────────────────────────────
async function loadMappings() {
    try {
        const p = await (await fetch('/api/mappings', { cache:'no-store' })).json();
        const m = p.mappings || [];
        el('mapCount').textContent = m.length + (m.length===1?' tag':' tags');
        el('mappedList').innerHTML = m.length ? m.map(t => {
            const item = t.daItemId||t.DaItemId, name = t.displayName||t.DisplayName||item, dt = t.dataType||t.DataType||'Auto';
            return `<div class="li" style="flex-direction:row;align-items:center;gap:10px">
                <div style="flex:1"><div class="n">${esc(name)}</div><div class="p">${esc(item)} &middot; ${esc(dt)}</div></div>
                <button class="btn ghost" style="padding:3px 9px" onclick="removeMapping(${JSON.stringify(item)})">Remove</button></div>`;
        }).join('') : '<span class="msg">No mapped tags. Add some from the browser.</span>';
    } catch(e) { el('mappedList').innerHTML = `<span class="bad">${esc(e.message)}</span>`; }
}
async function addTag(itemId, name) {
    try {
        await fetch('/api/mappings/add', { method:'POST', headers:{'Content-Type':'application/json'},
            body: JSON.stringify({ tags: [{ daItemId: itemId, displayName: name || itemId, dataType: 'Auto' }] }) });
        await loadMappings();
    } catch(e) { alert('Add failed: ' + e.message); }
}
async function addManual() {
    const v = el('manualItem').value.trim();
    if (!v) return;
    await addTag(v, v);
    el('manualItem').value = '';
}
async function removeMapping(itemId) {
    try {
        await fetch('/api/mappings/remove', { method:'POST', headers:{'Content-Type':'application/json'},
            body: JSON.stringify({ daItemId: itemId }) });
        await loadMappings();
    } catch(e) { alert('Remove failed: ' + e.message); }
}

// ── Init ──────────────────────────────────────────────────────────
el('modeApply').addEventListener('click', applyMode);
const initTab = location.hash.slice(1);
if (['monitor','connection','tags'].includes(initTab)) showTab(initTab);
loadServerConfig();
loadMappings();
refresh();
setInterval(refresh, 1000);
</script>
</body>
</html>
""";

    public static string FullHtml => Html + Script;
}
