#!/usr/bin/env python3
"""Render RSS trend CSV (from rss-trend.sh) as an SVG chart + summary stats.

Usage: python3 rss-trend.py [rss-trend.csv] [out.svg]
Stdlib only — no matplotlib needed.
"""
import csv
import sys
from collections import defaultdict

CSV_PATH = sys.argv[1] if len(sys.argv) > 1 else "rss-trend.csv"
SVG_PATH = sys.argv[2] if len(sys.argv) > 2 else "rss-trend.svg"

series = defaultdict(list)  # container -> [(t_seconds, rss_mib, cpu_pct)]
t0 = None
with open(CSV_PATH) as f:
    for row in csv.DictReader(f):
        name = row["container"]
        try:
            t = float(__import__("datetime").datetime.strptime(row["timestamp"], "%Y-%m-%dT%H:%M:%SZ").timestamp())
            mib = float(row["rss_mib"])
            cpu = float(row["cpu_pct"])
        except (KeyError, ValueError):
            continue
        if t0 is None:
            t0 = t
        series[name].append((t - t0, mib, cpu))

if not series:
    print(f"no rows in {CSV_PATH}")
    sys.exit(1)

# --- summary + linear trend (least squares, MiB/min) ---
print(f"{'container':<18} {'first':>8} {'last':>8} {'min':>8} {'max':>8} {'slope MiB/min':>14} {'cpu avg':>8}")
slopes = {}
for name, pts in sorted(series.items()):
    xs = [p[0] / 60.0 for p in pts]
    ys = [p[1] for p in pts]
    n = len(pts)
    xm, ym = sum(xs) / n, sum(ys) / n
    slope = sum((x - xm) * (y - ym) for x, y in zip(xs, ys)) / max(1e-9, sum((x - xm) ** 2 for x in xs))
    slopes[name] = slope
    cpu_avg = sum(p[2] for p in pts) / n
    print(f"{name:<18} {ys[0]:>7.1f}MiB {ys[-1]:>7.1f}MiB {min(ys):>7.1f}MiB {max(ys):>7.1f}MiB {slope:>13.2f} {cpu_avg:>7.1f}%")

# --- SVG ---
W, H, PAD_L, PAD_R, PAD_T, PAD_B = 900, 420, 70, 20, 30, 40
all_mib = [p[1] for pts in series.values() for p in pts]
all_t = [p[0] for pts in series.values() for p in pts]
ymax = max(all_mib) * 1.05 or 1
xmax = max(all_t) or 1
colors = ["#e6194b", "#3cb44b", "#4363d8", "#f58231", "#911eb4", "#42d4f4", "#f032e6", "#9a6324"]
plot_w, plot_h = W - PAD_L - PAD_R, H - PAD_T - PAD_B

def sx(t): return PAD_L + (t / xmax) * plot_w
def sy(m): return PAD_T + plot_h - (m / ymax) * plot_h

parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">',
         f'<rect width="{W}" height="{H}" fill="white"/>',
         f'<text x="{PAD_L}" y="18" font-size="14" font-family="monospace" font-weight="bold">RSS trend (MiB) — {CSV_PATH}</text>']
for g in range(5):
    gy = PAD_T + plot_h * g / 4
    val = ymax * (4 - g) / 4
    parts.append(f'<line x1="{PAD_L}" y1="{gy:.1f}" x2="{W - PAD_R}" y2="{gy:.1f}" stroke="#ddd"/>')
    parts.append(f'<text x="{PAD_L - 6}" y="{gy + 4:.1f}" font-size="10" font-family="monospace" text-anchor="end">{val:.0f}</text>')
for i, (name, pts) in enumerate(sorted(series.items())):
    color = colors[i % len(colors)]
    poly = " ".join(f"{sx(p[0]):.1f},{sy(p[1]):.1f}" for p in pts)
    parts.append(f'<polyline points="{poly}" fill="none" stroke="{color}" stroke-width="1.5"/>')
    last = pts[-1]
    label = f"{name} ({pts[-1][1]:.0f} MiB)"
    parts.append(f'<text x="{sx(last[0]) + 4}" y="{sy(last[1]) + 4}" font-size="11" font-family="monospace" fill="{color}">{label}</text>')
parts.append(f'<text x="{PAD_L}" y="{H - 8}" font-size="10" font-family="monospace">minutes: 0.0 → {xmax / 60:.1f} (slope in table = MiB/min; ~0 means no leak)</text>')
parts.append("</svg>")

with open(SVG_PATH, "w") as f:
    f.write("\n".join(parts))
print(f"\nchart written to {SVG_PATH} (open in a browser)")
