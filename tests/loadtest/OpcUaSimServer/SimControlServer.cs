using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

namespace OpcUaSimServer;

/// <summary>
/// Embedded HTTP control API + tag dashboard for the sim.
/// Serves:
///   GET  /                         — HTML dashboard
///   GET  /api/config               — global rate, node count, min rate
///   GET  /api/tags?offset&limit&q  — tag table (name, value, rate, frozen, writeable)
///   POST /api/tags/{number}/rate   — body { "rateMs": 250 } — set one tag's update rate
///   POST /api/config/rate          — body { "rateMs": 250 } — set the global default rate
/// Runs on SIM_CTRL_PORT (default 49331).
/// </summary>
internal static class SimControlServer
{
    public static async Task RunAsync(SimServer server, int port, CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        WebApplication app = builder.Build();

        app.MapGet("/", (HttpContext context) =>
        {
            // Never cache the dashboard: the JS is updated often and a stale page
            // (e.g. the pre-focus-fix version) looks like "rates can't be changed".
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            return Results.Text(DashboardHtml, "text/html; charset=utf-8");
        });

        app.MapGet("/api/config", () => Results.Json(new
        {
            nodeCount = server.NodeCount,
            globalRateMs = server.GlobalRateMs,
            minRateMs = server.GetMinRateMs()
        }));

        app.MapGet("/api/tags", (int? offset, int? limit, string? q) =>
        {
            TagSnapshot snapshot = server.GetTags(offset ?? 0, limit ?? 200, q);
            return Results.Json(new
            {
                total = snapshot.Total,
                offset = offset ?? 0,
                limit = limit ?? 200,
                tags = snapshot.Tags
            });
        });

        app.MapPost("/api/tags/{number:int}/rate", (int number, TagRateRequest body) =>
        {
            if (body.RateMs is null)
            {
                return Results.BadRequest(new { error = "rateMs is required" });
            }

            bool ok = server.SetTagRate(number, body.RateMs.Value);
            return ok
                ? Results.Ok(new { ok = true, number, rateMs = body.RateMs.Value })
                : Results.NotFound(new { error = $"tag {number} out of range" });
        });

        // "Apply to all": sets every tag's rate (and the global default), so the
        // change is immediately visible on all tags — not just the default for new ones.
        app.MapPost("/api/config/rate", (GlobalRateRequest body) =>
        {
            if (body.RateMs is null)
            {
                return Results.BadRequest(new { error = "rateMs is required" });
            }

            server.SetAllRates(body.RateMs.Value);
            return Results.Ok(new { ok = true, globalRateMs = server.GlobalRateMs });
        });

        Console.WriteLine($"Control dashboard: http://0.0.0.0:{port}/");
        await app.RunAsync(cancellationToken);
    }

    private sealed record TagRateRequest(int? RateMs);

    private sealed record GlobalRateRequest(int? RateMs);

    private const string DashboardHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>OPC UA Sim — Tag Control</title>
        <style>
          :root { color-scheme: dark; }
          body { font-family: system-ui, sans-serif; margin: 0; background: #12151c; color: #e6e8ee; }
          header { padding: 14px 20px; background: #1a1f2b; border-bottom: 1px solid #2a3142; display: flex; gap: 18px; align-items: center; flex-wrap: wrap; }
          h1 { font-size: 16px; margin: 0; font-weight: 600; }
          .pill { background: #232a3a; border: 1px solid #33405c; border-radius: 999px; padding: 3px 10px; font-size: 12px; color: #aab4cc; }
          .pill b { color: #7fd1a8; font-weight: 600; }
          main { padding: 16px 20px; }
          .controls { display: flex; gap: 10px; align-items: center; margin-bottom: 12px; flex-wrap: wrap; }
          input[type=text], input[type=number] { background: #1a1f2b; border: 1px solid #33405c; border-radius: 6px; color: #e6e8ee; padding: 5px 8px; font-size: 13px; }
          button { background: #2e6b4f; border: none; border-radius: 6px; color: #eaf7f0; padding: 6px 12px; font-size: 13px; cursor: pointer; }
          button:hover { background: #38805f; }
          table { border-collapse: collapse; width: 100%; font-size: 13px; }
          th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid #232a3a; }
          th { color: #8b95ad; font-weight: 500; position: sticky; top: 0; background: #12151c; }
          td input { width: 72px; }
          .frozen { color: #e5a06b; }
          .warn { color: #e5a06b; font-size: 12px; }
          .ok { color: #7fd1a8; }
          .bar { display: flex; gap: 14px; align-items: baseline; margin-bottom: 10px; }
          .bar span { color: #8b95ad; font-size: 12px; }
          .flash { color: #7fd1a8; font-size: 12px; min-height: 16px; }
        </style>
        </head>
        <body>
        <header>
          <h1>OPC UA Sim — Tag Control</h1>
          <span class="pill">nodes <b id="nodeCount">–</b></span>
          <span class="pill">global rate <b id="globalRate">–</b> ms</span>
          <span class="pill">min rate <b id="minRate">–</b> ms</span>
        </header>
        <main>
          <div class="controls">
            <input type="text" id="q" placeholder="Filter tag…" size="18">
            <button onclick="reload()">Refresh</button>
            <span style="flex:1"></span>
            <label>Global rate (ms): <input type="number" id="globalInput" min="10" step="10" style="width:90px"></label>
            <button onclick="setGlobal()">Apply to all</button>
          </div>
          <div class="flash" id="flash"></div>
          <table>
            <thead><tr><th>#</th><th>Tag</th><th>Value</th><th>Rate (ms)</th><th></th><th>State</th></tr></thead>
            <tbody id="rows"></tbody>
          </table>
        </main>
        <script>
        const PAGE = 200;
        let all = [];
        let refreshTimer = null;

        // The 2s auto-refresh re-renders the table, which would wipe any rate the
        // user is mid-editing. Pause it while an input/textarea has focus.
        function startRefresh() {
          if (refreshTimer) return;
          refreshTimer = setInterval(reload, 2000);
        }
        function stopRefresh() {
          if (refreshTimer) { clearInterval(refreshTimer); refreshTimer = null; }
        }
        document.addEventListener('focusin', (e) => {
          if (e.target && e.target.matches('input, textarea')) stopRefresh();
        });
        document.addEventListener('focusout', (e) => {
          if (e.target && e.target.matches('input, textarea')) startRefresh();
        });

        async function api(path, opts) {
          const r = await fetch(path, opts);
          if (!r.ok) throw new Error((await r.json()).error || r.status);
          return r.json();
        }

        function esc(s) { return String(s).replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }

        async function loadConfig() {
          const c = await api('/api/config');
          document.getElementById('nodeCount').textContent = c.nodeCount;
          document.getElementById('globalRate').textContent = c.globalRateMs;
          document.getElementById('minRate').textContent = c.minRateMs;
          document.getElementById('globalInput').value = c.globalRateMs;
        }

        async function loadTags() {
          const q = document.getElementById('q').value.trim();
          const d = await api('/api/tags?offset=0&limit=' + PAGE + (q ? '&q=' + encodeURIComponent(q) : ''));
          all = d.tags;
          render();
        }

        function render() {
          const tbody = document.getElementById('rows');
          tbody.innerHTML = '';
          if (all.length === 0) {
            tbody.innerHTML = '<tr><td colspan="6" style="color:#8b95ad">No tags match.</td></tr>';
            return;
          }
          for (const t of all) {
            const tr = document.createElement('tr');
            const state = t.frozen ? '<span class="frozen">frozen</span>' : (t.writeable ? '<span class="ok">writeable</span>' : '<span>ok</span>');
            tr.innerHTML =
              '<td>' + t.number + '</td>' +
              '<td><b>' + esc(t.name) + '</b></td>' +
              '<td>' + (Number.isFinite(t.value) ? t.value.toFixed(3) : '–') + '</td>' +
              '<td><input type="number" min="10" step="10" value="' + t.rateMs + '" data-num="' + t.number + '"></td>' +
              '<td><button data-apply="' + t.number + '">Set</button></td>' +
              '<td>' + state + '</td>';
            tbody.appendChild(tr);
          }
          tbody.querySelectorAll('button[data-apply]').forEach(b =>
            b.addEventListener('click', () => setTag(+b.dataset.apply)));
        }

        async function setTag(number) {
          const input = document.querySelector('input[data-num="' + number + '"]');
          const rateMs = +input.value;
          if (!Number.isFinite(rateMs) || rateMs < 10) { flash('rate must be ≥ 10 ms'); return; }
          const r = await api('/api/tags/' + number + '/rate', {
            method: 'POST', headers: {'Content-Type':'application/json'},
            body: JSON.stringify({ rateMs })
          });
          flash('Tag' + String(number).padStart(5,'0') + ' → ' + r.rateMs + ' ms');
          loadConfig(); loadTags();
        }

        async function setGlobal() {
          const rateMs = +document.getElementById('globalInput').value;
          if (!Number.isFinite(rateMs) || rateMs < 10) { flash('rate must be ≥ 10 ms'); return; }
          const r = await api('/api/config/rate', {
            method: 'POST', headers: {'Content-Type':'application/json'},
            body: JSON.stringify({ rateMs })
          });
          flash('Global rate → ' + r.globalRateMs + ' ms');
          loadConfig(); loadTags();
        }

        function flash(msg) { const f = document.getElementById('flash'); f.textContent = msg; }

        async function reload() { try { await Promise.all([loadConfig(), loadTags()]); } catch (e) { flash('error: ' + e.message); } }

        document.getElementById('q').addEventListener('input', () => { clearTimeout(window._t); window._t = setTimeout(loadTags, 250); });
        reload();
        startRefresh();
        </script>
        </body>
        </html>
        """;
}
