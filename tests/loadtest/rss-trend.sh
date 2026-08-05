#!/usr/bin/env bash
# Sample container RSS (and CPU) into a CSV for trend analysis.
#
# Usage: rss-trend.sh [interval-secs] [samples] [container...]
#   ./rss-trend.sh 10 60 opcbridge-fix opcua-sim-20k
# Env: RSS_TREND_OUT=path (default ./rss-trend.csv)
set -euo pipefail

INTERVAL="${1:-10}"
COUNT="${2:-60}"
shift 2 || true
CONTAINERS=("$@")
if [ "${#CONTAINERS[@]}" -eq 0 ]; then
  CONTAINERS=(opcbridge-fix opcua-sim-20k opcbridge-lt)
fi
OUT="${RSS_TREND_OUT:-rss-trend.csv}"

echo "timestamp,container,rss_mib,cpu_pct" > "$OUT"
echo "sampling [${CONTAINERS[*]}] every ${INTERVAL}s x ${COUNT} -> $OUT (Ctrl-C to stop early)"

for i in $(seq 1 "$COUNT"); do
  TS=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  docker stats --no-stream --format '{{.Name}}|{{.MemUsage}}|{{.CPUPerc}}' "${CONTAINERS[@]}" 2>/dev/null \
    | while IFS='|' read -r name mem cpu; do
        mib=$(printf '%s' "$mem" | awk -F/ '{v=$1; u=substr(v,length(v)-1); n=substr(v,1,length(v)-2); if (u=="Gi") printf "%.1f", n*1024; else if (u=="Ki") printf "%.1f", n/1024; else printf "%.1f", n}')
        pct=$(printf '%s' "$cpu" | tr -d '%')
        printf '%s,%s,%s,%s\n' "$TS" "$name" "$mib" "$pct"
      done >> "$OUT"
  [ "$i" -lt "$COUNT" ] && sleep "$INTERVAL"
done
echo "done. plot with: python3 rss-trend.py $OUT"
