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
            --bg:       #0d1117;
            --surface:  #161b22;
            --surface2: #1c2330;
            --border:   #30363d;
            --text:     #e6edf3;
            --muted:    #8b949e;
            --good:     #3fb950;
            --bad:      #f85149;
            --warn:     #d29922;
            --accent:   #58a6ff;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { background: var(--bg); color: var(--text); font-size: 14px; line-height: 1.5; }

        /* ── Header ─────────────────────────────── */
        header {
            padding: 14px 28px;
            background: var(--surface);
            border-bottom: 1px solid var(--border);
            display: flex; align-items: center; gap: 14px;
            position: sticky; top: 0; z-index: 10;
        }
        .pulse-dot {
            width: 9px; height: 9px; border-radius: 50%; flex-shrink: 0;
            background: var(--good);
            box-shadow: 0 0 0 0 rgba(63,185,80,.6);
            animation: pulse 2s infinite;
        }
        .pulse-dot.offline { background: var(--bad); animation: none; box-shadow: none; }
        @keyframes pulse {
            0%   { box-shadow: 0 0 0 0   rgba(63,185,80,.6); }
            70%  { box-shadow: 0 0 0 7px rgba(63,185,80,0);  }
            100% { box-shadow: 0 0 0 0   rgba(63,185,80,0);  }
        }
        header h1 { font-size: 17px; font-weight: 600; }
        .header-meta { color: var(--muted); font-size: 12px; }
        .header-meta code {
            color: var(--accent); background: rgba(88,166,255,.1);
            padding: 1px 5px; border-radius: 4px; font-size: 11px;
        }
        .header-right { margin-left: auto; color: var(--muted); font-size: 12px; white-space: nowrap; }

        /* ── Layout ─────────────────────────────── */
        main { padding: 24px 28px; max-width: 1400px; }

        /* ── Cards ──────────────────────────────── */
        .cards {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
            gap: 10px; margin-bottom: 28px;
        }
        .card {
            background: var(--surface); border: 1px solid var(--border);
            border-radius: 8px; padding: 14px 16px;
        }
        .card-label {
            color: var(--muted); font-size: 11px;
            text-transform: uppercase; letter-spacing: .06em; font-weight: 500;
        }
        .card-value { margin-top: 8px; font-size: 17px; font-weight: 700; line-height: 1.2; overflow-wrap: anywhere; }
        .card-sub   { margin-top: 5px; color: var(--muted); font-size: 12px; overflow-wrap: anywhere; }

        /* ── Badge ──────────────────────────────── */
        .badge {
            display: inline-flex; align-items: center; gap: 5px;
            padding: 2px 9px; border-radius: 12px; font-size: 13px; font-weight: 600;
        }
        .badge::before { content:''; width:6px; height:6px; border-radius:50%; background:currentColor; opacity:.75; }
        .badge.good { color: var(--good); background: rgba(63,185,80,.12); }
        .badge.bad  { color: var(--bad);  background: rgba(248,81,73,.12);  }
        .badge.warn { color: var(--warn); background: rgba(210,153,34,.12); }

        /* ── Section header ─────────────────────── */
        .sh {
            font-size: 11px; font-weight: 600; color: var(--muted);
            text-transform: uppercase; letter-spacing: .08em;
            margin: 20px 0 10px; padding-bottom: 6px;
            border-bottom: 1px solid var(--border);
        }
        .sh:first-child { margin-top: 0; }

        /* ── Panel / table wrap ──────────────────── */
        .panel { border: 1px solid var(--border); border-radius: 8px; overflow: hidden; margin-bottom: 24px; }
        table { width: 100%; border-collapse: collapse; background: var(--surface); }
        thead { z-index: 1; }
        th {
            background: var(--surface2); color: var(--muted);
            font-size: 11px; text-transform: uppercase; letter-spacing: .06em; font-weight: 500;
            padding: 9px 14px; text-align: left; border-bottom: 1px solid var(--border);
        }
        td { padding: 10px 14px; border-bottom: 1px solid var(--border); vertical-align: middle; }
        tr:last-child td { border-bottom: none; }
        tbody tr:hover td { background: var(--surface2); transition: background .1s; }
        td.lbl { color: var(--muted); width: 160px; font-size: 13px; }

        .good { color: var(--good); }
        .bad  { color: var(--bad);  }
        .warn { color: var(--warn); }
        code  { color: var(--accent); font-size: 12px; }

        /* ── Inputs ─────────────────────────────── */
        .row { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
        select, input[type="text"] {
            background: var(--surface2); color: var(--text);
            border: 1px solid var(--border); border-radius: 6px;
            padding: 6px 10px; font-size: 13px;
        }
        select { cursor: pointer; }
        select:focus, input[type="text"]:focus {
            outline: 2px solid var(--accent); outline-offset: 1px; border-color: transparent;
        }
        input[type="text"] { width: 240px; }

        /* ── Buttons ─────────────────────────────── */
        .btn {
            display: inline-flex; align-items: center; gap: 6px;
            background: var(--accent); color: #0d1117;
            border: none; border-radius: 6px;
            padding: 6px 14px; font-size: 13px; font-weight: 600;
            cursor: pointer; transition: opacity .15s; white-space: nowrap;
        }
        .btn:hover:not(:disabled) { opacity: .85; }
        .btn:disabled { opacity: .4; cursor: not-allowed; }
        .btn-ghost {
            background: var(--surface2); color: var(--text);
            border: 1px solid var(--border);
        }
        .btn-ghost:hover:not(:disabled) { border-color: var(--accent); color: var(--accent); opacity: 1; }
        .msg { font-size: 13px; color: var(--muted); }

        /* ── Endpoint box ────────────────────────── */
        .endpoint-box {
            background: var(--surface2); border: 1px solid var(--border);
            border-radius: 6px; padding: 9px 14px;
            font-family: 'Consolas', 'SF Mono', monospace; font-size: 13px;
            color: var(--accent); word-break: break-all; margin-bottom: 24px;
        }

        /* ── Tabs ────────────────────────────────── */
        .tabs { display: flex; border-bottom: 1px solid var(--border); background: var(--surface2); }
        .tab {
            background: none; color: var(--muted);
            border: none; border-bottom: 2px solid transparent;
            border-radius: 0; padding: 10px 20px;
            font-size: 13px; font-weight: 500; cursor: pointer;
            transition: color .15s;
        }
        .tab.active { color: var(--text); border-bottom-color: var(--accent); }
        .tab:hover:not(.active) { color: var(--text); }
        .tab-pane { padding: 14px 16px; }

        /* ── Server list ─────────────────────────── */
        .server-list { display: flex; flex-direction: column; gap: 4px; }
        .srv {
            display: flex; flex-direction: column;
            padding: 10px 12px; border-radius: 6px; cursor: pointer;
            border: 1px solid transparent; transition: background .1s, border-color .1s;
        }
        .srv:hover   { background: var(--surface2); border-color: var(--border); }
        .srv.active  { background: rgba(88,166,255,.08); border-color: var(--accent); }
        .srv-name    { font-size: 13px; font-weight: 600; }
        .srv-prog    { font-size: 12px; color: var(--muted); font-family: monospace; margin-top: 2px; }

        /* ── Selected bar ────────────────────────── */
        .sel-bar {
            border-top: 1px solid var(--border);
            padding: 12px 16px; background: var(--surface2);
            display: flex; align-items: center; gap: 8px; flex-wrap: wrap;
        }
        .sel-label { font-size: 11px; font-weight: 600; color: var(--muted); text-transform: uppercase; letter-spacing: .06em; white-space: nowrap; }

        /* ── SVG icons ───────────────────────────── */
        .icon { width: 14px; height: 14px; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; flex-shrink: 0; }
        /* ── Tag tree ────────────────────────────── */
        .tnode {
            display: flex; align-items: center; gap: 8px;
            padding: 6px 8px; border-radius: 5px; cursor: pointer; font-size: 13px;
        }
        .tnode:hover { background: var(--surface2); }
        .tnode .icon { color: var(--muted); }
        .tnode.branch .icon { color: var(--warn); }
        .tnode.tag    .icon { color: var(--accent); }
        .tnode .tprog { color: var(--muted); font-family: monospace; font-size: 11px; margin-left: auto; }
        .tcrumb { color: var(--accent); cursor: pointer; }
        .tcrumb:hover { text-decoration: underline; }
    </style>
</head>
<body>
<header>
    <div class="pulse-dot" id="pulseDot"></div>
    <div>
        <h1>OPC DA → UA Bridge</h1>
        <div class="header-meta">Runtime dashboard &nbsp;·&nbsp; UA: <code id="headerEndpoint">—</code></div>
    </div>
    <div class="header-right" id="refresh">—</div>
</header>

<main>

    <!-- Status cards -->
    <div class="cards">
        <div class="card">
            <div class="card-label">Bridge runtime</div>
            <div class="card-value" id="bridgeState">—</div>
            <div class="card-sub" id="lastError">No errors</div>
        </div>
        <div class="card">
            <div class="card-label">OPC DA client</div>
            <div class="card-value" id="daState">—</div>
            <div class="card-sub" id="daMode">Mode: —</div>
        </div>
        <div class="card">
            <div class="card-label">Last DA read</div>
            <div class="card-value" id="lastDaRead">—</div>
            <div class="card-sub" id="lastDaReadCount">0 values</div>
        </div>
        <div class="card">
            <div class="card-label">Last UA write</div>
            <div class="card-value" id="lastUaWrite">—</div>
            <div class="card-sub" id="lastUaWriteCount">0 values</div>
        </div>
        <div class="card">
            <div class="card-label">OPC UA server</div>
            <div class="card-value" id="uaState">—</div>
            <div class="card-sub" id="uaClients">0 clients</div>
        </div>
        <div class="card">
            <div class="card-label">Configuration</div>
            <div class="card-value" id="mappingCount">0 tags</div>
            <div class="card-sub" id="updateRate">Update: — ms</div>
        </div>
    </div>

    <!-- UA Endpoint -->
    <p class="sh">OPC UA Endpoint</p>
    <div class="endpoint-box" id="uaEndpoint">—</div>

    <!-- DA Mode -->    <p class="sh">OPC DA Mode</p>
    <div class="panel" style="margin-bottom:24px">
        <table><tbody>
            <tr>
                <td class="lbl">Source mode</td>
                <td>
                    <div class="row">
                        <select id="modeSelect">
                            <option value="Simulation">Simulation</option>
                            <option value="OpcDa">Real OPC DA</option>
                        </select>
                        <button class="btn" id="modeApply" type="button">
                            <svg class="icon" viewBox="0 0 24 24"><polyline points="20 6 9 17 4 12"/></svg>
                            Apply
                        </button>
                        <span class="msg" id="modeMessage">No pending change</span>
                    </div>
                </td>
            </tr>
        </tbody></table>
    </div>

    <!-- Server Browser -->
    <p class="sh">OPC DA Server Browser</p>
    <div class="panel" style="margin-bottom:24px">
        <div class="tabs">
            <button class="tab active" id="tabLocal"  onclick="switchTab('local')"  type="button">Local</button>
            <button class="tab"        id="tabRemote" onclick="switchTab('remote')" type="button">Remote</button>
        </div>

        <div class="tab-pane" id="paneLocal">
            <div class="row" style="margin-bottom:12px">
                <button class="btn btn-ghost" id="btnReloadLocal" type="button" onclick="browseServers(null)">
                    <svg class="icon" viewBox="0 0 24 24"><polyline points="23 4 23 10 17 10"/><polyline points="1 20 1 14 7 14"/><path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/></svg>
                    Reload
                </button>
                <span class="msg" id="msgLocal">Click Reload to scan local OPC DA servers.</span>
            </div>
            <div class="server-list" id="listLocal"></div>
        </div>

        <div class="tab-pane" id="paneRemote" style="display:none">
            <div class="row" style="margin-bottom:12px">
                <input id="remoteHost" type="text" placeholder="192.168.x.x or hostname" style="width:220px">
                <button class="btn btn-ghost" id="btnReloadRemote" type="button" onclick="browseServers(el('remoteHost').value)">
                    <svg class="icon" viewBox="0 0 24 24"><polyline points="23 4 23 10 17 10"/><polyline points="1 20 1 14 7 14"/><path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/></svg>
                    Browse
                </button>
                <span class="msg" id="msgRemote">Enter a host and click Browse.</span>
            </div>
            <div class="server-list" id="listRemote"></div>
        </div>

        <div class="sel-bar">
            <span class="sel-label">Selected</span>
            <input id="cfgProgId" type="text" placeholder="ProgID" style="flex:1;min-width:200px">
            <input id="cfgHost"   type="text" placeholder="Host" style="width:150px" oninput="toggleCredentials()">
            <span class="sel-label" id="credLabel" style="display:none">Credentials</span>
            <input id="cfgUser"   type="text"     placeholder="Username" style="width:130px;display:none">
            <input id="cfgPass"   type="password" placeholder="Password" style="width:120px;display:none">
            <input id="cfgDomain" type="text"     placeholder="Domain (optional)" style="width:150px;display:none">
            <button class="btn" id="cfgApply" type="button" onclick="applyServerConfig()">
                <svg class="icon" viewBox="0 0 24 24"><polyline points="20 6 9 17 4 12"/></svg>
                Apply
            </button>
            <span class="msg" id="cfgMessage">—</span>
        </div>
    </div>

    <!-- Tag browser -->
    <p class="sh">OPC DA Tag Browser</p>
    <div class="panel" style="margin-bottom:24px">
        <div class="row" style="padding:14px 16px; border-bottom:1px solid var(--border)">
            <button class="btn btn-ghost" id="btnBrowseTags" type="button" onclick="browseTags('')">
                <svg class="icon" viewBox="0 0 24 24"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>
                Browse tags
            </button>
            <span class="msg" id="tagBreadcrumb">Uses the Selected server above (ProgID + Host).</span>
        </div>
        <div id="tagTree" style="padding:10px 16px; max-height:340px; overflow-y:auto;">
            <span style="color:var(--muted);font-size:13px">Click "Browse tags" to explore the selected server's address space.</span>
        </div>
    </div>

    <!-- Live values -->
    <p class="sh">Live Values &#8212; OPC DA &#8594; Bridge</p>
    <div class="panel">
        <table>
            <thead><tr>
                <th>DA Item ID</th><th>Value</th><th>Quality</th><th>Timestamp (local)</th>
            </tr></thead>
            <tbody id="values"><tr><td colspan="4" style="color:var(--muted)">Waiting for values…</td></tr></tbody>
        </table>
    </div>

</main>
""";

    // JS is appended below to keep each part manageable
    public const string Script = """
<script>
const ESC = {'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'};
const esc = s => String(s??'').replace(/[&<>'"]/g, c => ESC[c]);
const el  = id => document.getElementById(id);

let modeInFlight = false;

// ── Helpers ──────────────────────────────────────────────────────
function badge(text, cls) {
    return `<span class="badge ${cls}">${esc(text)}</span>`;
}
function stateClass(v) {
    if (!v) return 'warn';
    const s = v.toLowerCase();
    if (s === 'running' || s === 'connected') return 'good';
    if (s === 'faulted' || s === 'stopped'  || s === 'disconnected') return 'bad';
    return 'warn';
}
function relTime(utcStr) {
    if (!utcStr) return '—';
    const d = Math.floor((Date.now() - new Date(utcStr)) / 1000);
    if (d < 5)    return 'just now';
    if (d < 60)   return d + 's ago';
    if (d < 3600) return Math.floor(d/60) + 'm ago';
    return new Date(utcStr).toLocaleTimeString();
}
function locTime(utcStr) { return utcStr ? new Date(utcStr).toLocaleString() : '—'; }
function get(o, k) { return o[k] ?? o[k[0].toUpperCase() + k.slice(1)]; }

// ── Dashboard refresh ─────────────────────────────────────────────
async function refreshDashboard() {
    try {
        const r = await fetch('/api/dashboard', { cache: 'no-store' });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const p = await r.json();
        const b  = p.bridge || p.Bridge || {};
        const ua = p.ua     || p.Ua     || {};
        const vs = p.values || p.Values || [];

        el('pulseDot').className = 'pulse-dot';
        el('refresh').textContent = 'Updated ' + new Date().toLocaleTimeString();

        const ep = get(ua, 'endpointUrl') || '—';
        el('headerEndpoint').textContent = ep;
        el('uaEndpoint').textContent = ep;

        el('bridgeState').innerHTML = badge(get(b,'bridgeState') || '—', stateClass(get(b,'bridgeState')));
        const err = get(b,'lastError');
        el('lastError').textContent  = err || 'No errors';
        el('lastError').className    = 'card-sub' + (err ? ' bad' : '');

        el('daState').innerHTML = badge(get(b,'daConnectionState') || '—', stateClass(get(b,'daConnectionState')));
        el('daMode').textContent = 'Mode: ' + (get(b,'daMode') || '—');

        el('lastDaRead').textContent     = relTime(get(b,'lastDaReadUtc'));
        el('lastDaReadCount').textContent = (get(b,'lastDaReadCount')??0) + ' values';
        el('lastUaWrite').textContent     = relTime(get(b,'lastUaWriteUtc'));
        el('lastUaWriteCount').textContent= (get(b,'lastUaWriteCount')??0) + ' values';

        el('uaState').innerHTML  = badge(get(ua,'state') || '—', stateClass(get(ua,'state')));
        el('uaClients').textContent = (get(ua,'connectedClientCount')??0) + ' clients';
        el('mappingCount').textContent = (get(b,'mappingCount')??0) + ' tags';
        el('updateRate').textContent   = 'Update: ' + (get(b,'updateRateMs')||'—') + ' ms';

        if (!modeInFlight) el('modeSelect').value = get(b,'daMode') || 'Simulation';

        el('values').innerHTML = vs.length
            ? vs.map(item => {
                const good = get(item,'isGood'), q = get(item,'daQuality');
                return `<tr>
                    <td><code>${esc(get(item,'daItemId'))}</code></td>
                    <td>${esc(String(get(item,'value')??''))}</td>
                    <td>${badge(good?'Good':'Bad', good?'good':'bad')} <span class="${good?'good':'bad'}" style="font-size:11px;opacity:.7">(${q})</span></td>
                    <td style="color:var(--muted);font-size:13px">${esc(locTime(get(item,'timestampUtc')))}</td>
                </tr>`;
              }).join('')
            : '<tr><td colspan="4" style="color:var(--muted)">No values yet.</td></tr>';

    } catch(e) {
        el('pulseDot').className = 'pulse-dot offline';
        el('refresh').textContent = 'Offline — ' + e.message;
        el('values').innerHTML = `<tr><td colspan="4" class="bad">${esc(e.message)}</td></tr>`;
    }
}

// ── Mode change ───────────────────────────────────────────────────
async function applyModeChange() {
    const sel = el('modeSelect'), btn = el('modeApply'), msg = el('modeMessage');
    modeInFlight = true; sel.disabled = btn.disabled = true;
    msg.textContent = 'Applying…'; msg.className = 'msg warn';
    try {
        const r = await fetch('/api/da/mode', {
            method: 'POST', headers: {'Content-Type':'application/json'},
            body: JSON.stringify({ mode: sel.value })
        });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const p = await r.json();
        msg.textContent = '✓ Mode set to ' + (p.mode || sel.value);
        msg.className = 'msg good';
        await refreshDashboard();
    } catch(e) {
        msg.textContent = '✗ ' + e.message; msg.className = 'msg bad';
    } finally {
        modeInFlight = false; sel.disabled = btn.disabled = false;
    }
}

// ── Server browser ────────────────────────────────────────────────
function switchTab(tab) {
    const loc = tab === 'local';
    el('tabLocal').classList.toggle('active', loc);
    el('tabRemote').classList.toggle('active', !loc);
    el('paneLocal').style.display  = loc ? '' : 'none';
    el('paneRemote').style.display = loc ? 'none' : '';
}

async function browseServers(host) {
    const local = !host || !host.trim();
    const listEl = el(local ? 'listLocal'       : 'listRemote');
    const msgEl  = el(local ? 'msgLocal'         : 'msgRemote');
    const btnEl  = el(local ? 'btnReloadLocal'   : 'btnReloadRemote');
    const dhost  = local ? 'localhost' : host.trim();

    btnEl.disabled = true;
    msgEl.textContent = 'Scanning…'; msgEl.className = 'msg warn';
    listEl.innerHTML = '<span style="color:var(--muted);font-size:13px">Scanning…</span>';

    try {
        const url = '/api/da/servers' + (local ? '' : '?host=' + encodeURIComponent(dhost));
        const r = await fetch(url, { cache: 'no-store' });
        const p = await r.json();
        if (p.error) throw new Error(p.error);

        const svrs = p.servers || [];
        if (!svrs.length) {
            listEl.innerHTML = '<span style="color:var(--muted);font-size:13px">No OPC DA servers found.</span>';
            msgEl.textContent = '0 servers found'; msgEl.className = 'msg';
            return;
        }
        msgEl.textContent = svrs.length + (svrs.length > 1 ? ' servers' : ' server') + ' found';
        msgEl.className = 'msg good';

        listEl.innerHTML = svrs.map((s, i) => {
            const id   = `si-${local?'l':'r'}-${i}`;
            const name = s.description || s.Description || s.progId || s.ProgId;
            const prog = s.progId || s.ProgId;
            return `<div class="srv" id="${id}" onclick="selectServer(${JSON.stringify(prog)},${JSON.stringify(dhost)},'${id}')">
                <span class="srv-name">${esc(name)}</span>
                <span class="srv-prog">${esc(prog)}</span>
            </div>`;
        }).join('');
    } catch(e) {
        listEl.innerHTML = `<span style="color:var(--bad);font-size:13px">${esc(e.message)}</span>`;
        msgEl.textContent = '✗ ' + e.message; msgEl.className = 'msg bad';
    } finally { btnEl.disabled = false; }
}

function selectServer(progId, host, itemId) {
    document.querySelectorAll('.srv.active').forEach(n => n.classList.remove('active'));
    el(itemId)?.classList.add('active');
    el('cfgProgId').value = progId;
    el('cfgHost').value   = host;
    toggleCredentials();
    el('cfgMessage').textContent = 'Selected — click Apply to connect';
    el('cfgMessage').className   = 'msg warn';
}

// ── Server config apply ───────────────────────────────────────────
function toggleCredentials() {
    const isRemote = el('cfgHost').value.trim().toLowerCase() !== 'localhost' &&
                     el('cfgHost').value.trim() !== '';
    ['credLabel','cfgUser','cfgPass','cfgDomain'].forEach(id => {
        el(id).style.display = isRemote ? '' : 'none';
    });
}

async function loadServerConfig() {
    try {
        const r = await fetch('/api/da/config', { cache: 'no-store' });
        if (!r.ok) return;
        const p = await r.json();
        el('cfgProgId').value = p.progId || p.ProgId || '';
        el('cfgHost').value   = p.host   || p.Host   || 'localhost';
        el('cfgUser').value   = p.remoteUsername || '';
        el('cfgDomain').value = p.remoteDomain   || '';
        toggleCredentials();
    } catch {}
}

async function applyServerConfig() {
    const btn = el('cfgApply'), msg = el('cfgMessage');
    btn.disabled = true; msg.textContent = 'Saving…'; msg.className = 'msg warn';
    try {
        const body = {
            progId: el('cfgProgId').value.trim(),
            host:   el('cfgHost').value.trim() || 'localhost',
            remoteUsername: el('cfgUser').value.trim()   || null,
            remotePassword: el('cfgPass').value          || null,
            remoteDomain:   el('cfgDomain').value.trim() || null
        };
        const r = await fetch('/api/da/config', {
            method: 'POST', headers: {'Content-Type':'application/json'},
            body: JSON.stringify(body)
        });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        msg.textContent = '✓ Saved — switch to OpcDa mode to connect';
        msg.className = 'msg good';
    } catch(e) {
        msg.textContent = '✗ ' + e.message; msg.className = 'msg bad';
    } finally { btn.disabled = false; }
}

// ── Init ──────────────────────────────────────────────────────────
el('modeApply').addEventListener('click', applyModeChange);
// ── Tag browser ───────────────────────────────────────────────────
let tagPath = '';

function renderBreadcrumb() {
    const crumb = el('tagBreadcrumb');
    const parts = tagPath ? tagPath.split('.') : [];
    let html = `<span class="tcrumb" onclick="browseTags('')">root</span>`;
    let acc = '';
    for (const part of parts) {
        acc = acc ? acc + '.' + part : part;
        html += ` / <span class="tcrumb" onclick="browseTags(${JSON.stringify(acc)})">${esc(part)}</span>`;
    }
    crumb.innerHTML = html;
}

async function browseTags(path) {
    const btn = el('btnBrowseTags'), tree = el('tagTree');
    const progId = el('cfgProgId').value.trim();
    const host   = el('cfgHost').value.trim() || 'localhost';

    if (!progId) {
        tree.innerHTML = '<span class="bad" style="font-size:13px">Select a server first (set ProgID above).</span>';
        return;
    }

    tagPath = path || '';
    btn.disabled = true;
    tree.innerHTML = '<span style="color:var(--muted);font-size:13px">Browsing…</span>';

    try {
        const body = {
            progId, host, path: tagPath,
            remoteUsername: el('cfgUser').value.trim()   || null,
            remotePassword: el('cfgPass').value          || null,
            remoteDomain:   el('cfgDomain').value.trim() || null
        };
        const r = await fetch('/api/da/tags', {
            method: 'POST', headers: {'Content-Type':'application/json'},
            body: JSON.stringify(body)
        });
        const p = await r.json();
        if (p.error) throw new Error(p.error);

        renderBreadcrumb();
        const branches = p.branches || [];
        const tags     = p.tags     || [];

        let html = '';
        // "up" navigation
        if (tagPath) {
            const parent = tagPath.includes('.') ? tagPath.slice(0, tagPath.lastIndexOf('.')) : '';
            html += `<div class="tnode" onclick="browseTags(${JSON.stringify(parent)})">
                <svg class="icon" viewBox="0 0 24 24"><polyline points="15 18 9 12 15 6"/></svg>
                <span style="color:var(--muted)">.. (up)</span></div>`;
        }
        // branches (folders)
        for (const b of branches) {
            const child = tagPath ? tagPath + '.' + b : b;
            html += `<div class="tnode branch" onclick="browseTags(${JSON.stringify(child)})">
                <svg class="icon" viewBox="0 0 24 24"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>
                <span>${esc(b)}</span></div>`;
        }
        // tags (leaves)
        for (const t of tags) {
            const name = t.name || t.Name;
            const itemId = t.itemId || t.ItemId;
            html += `<div class="tnode tag" onclick="pickTag(${JSON.stringify(itemId)})" title="Click to copy ItemID to clipboard">
                <svg class="icon" viewBox="0 0 24 24"><path d="M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z"/><line x1="7" y1="7" x2="7.01" y2="7"/></svg>
                <span>${esc(name)}</span><span class="tprog">${esc(itemId)}</span></div>`;
        }
        if (!branches.length && !tags.length) {
            html += '<span style="color:var(--muted);font-size:13px">Empty.</span>';
        }
        tree.innerHTML = html;
    } catch(e) {
        tree.innerHTML = `<span class="bad" style="font-size:13px">${esc(e.message)}</span>`;
    } finally {
        btn.disabled = false;
    }
}

function pickTag(itemId) {
    navigator.clipboard?.writeText(itemId);
    const tree = el('tagTree');
    const note = document.createElement('div');
    note.className = 'msg good';
    note.style.padding = '6px 8px';
    note.textContent = '✓ Copied: ' + itemId;
    tree.prepend(note);
    setTimeout(() => note.remove(), 2500);
}

loadServerConfig();
refreshDashboard();
setInterval(refreshDashboard, 1000);
</script>
</body>
</html>
""";

    public static string FullHtml => Html + Script;
}
