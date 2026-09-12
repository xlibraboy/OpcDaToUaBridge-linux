#!/usr/bin/env bash
# Seed a realistic industrial SCADA tag set into a running OpcBridge instance so the
# HMI and Designer have something plant-like to connect to.
#
# Analog process values come from an OPC UA simulation source (each sim node is
# re-labelled with an engineering name/unit). On/off and mode status tags are read from
# the simulator's self-toggling Boolean nodes under Objects/Status, so the simulator must
# run with SIM_STATUS_TAGS="Name:PeriodMs,..." (see tests/loadtest/OpcUaSimServer).
#
# The script is idempotent: it bulk-adds (insert-only) then updates every tag, so
# re-running it only refreshes the existing mappings. Status tags seeded by an older
# version of this script as static Manual values are removed.
#
# Usage:
#   scripts/demo/seed-industrial-scada.sh [API_BASE_URL] [options]
#
# Options:
#   --source-id ID        Bridge source that holds the sim tags (default: ua-sim)
#   --sim-endpoint URL    OPC UA endpoint used when the source has to be created
#                         (default: opc.tcp://172.17.0.1:49321/opcuasim/)
#   --prune-stale         Remove the leftover demo mappings on the dead 'default'
#                         OPC DA source (DemoTag / Pump.Run / Valve.Open / Level.PV)
#   --no-influx           Do not enable InfluxDB history logging on analog tags
#   -h, --help            Show this help
#
# Examples:
#   scripts/demo/seed-industrial-scada.sh
#   scripts/demo/seed-industrial-scada.sh http://127.0.0.1:18081 --prune-stale

set -euo pipefail

API="http://127.0.0.1:18080"
SOURCE_ID="ua-sim"
SIM_ENDPOINT="opc.tcp://172.17.0.1:49321/opcuasim/"
PRUNE_STALE=0
ENABLE_INFLUX=1

print_help() {
  cat <<'USAGE'
Seed a realistic industrial SCADA tag set into a running OpcBridge instance.

Usage:
  scripts/demo/seed-industrial-scada.sh [API_BASE_URL] [options]

Options:
  --source-id ID        Bridge source that holds the sim tags (default: ua-sim)
  --sim-endpoint URL    OPC UA endpoint used when the source must be created
                        (default: opc.tcp://172.17.0.1:49321/opcuasim/)
  --prune-stale         Remove leftover demo mappings on the dead 'default'
                        OPC DA source (DemoTag / Pump.Run / Valve.Open / Level.PV)
  --no-influx           Do not enable InfluxDB history logging on analog tags
  -h, --help            Show this help

Examples:
  scripts/demo/seed-industrial-scada.sh
  scripts/demo/seed-industrial-scada.sh http://127.0.0.1:18081 --prune-stale
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help) print_help; exit 0 ;;
    --source-id) SOURCE_ID="${2:?--source-id needs a value}"; shift 2 ;;
    --sim-endpoint) SIM_ENDPOINT="${2:?--sim-endpoint needs a value}"; shift 2 ;;
    --prune-stale) PRUNE_STALE=1; shift ;;
    --no-influx) ENABLE_INFLUX=0; shift ;;
    http://*|https://*) API="$1"; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

API="${API%/}"

if ! command -v python3 >/dev/null 2>&1; then
  echo "[seed] python3 is required" >&2
  exit 1
fi

if ! curl -sf -m 5 "$API/api/status/ports" >/dev/null; then
  echo "[seed] no OpcBridge answering at $API" >&2
  exit 1
fi

# ---- 1. Make sure the OPC UA simulation source exists -------------------------
source_exists="$(curl -sf -m 5 "$API/api/da/sources" | python3 -c '
import json, sys
target = sys.argv[1]
data = json.load(sys.stdin)
ids = {s.get("sourceId") for s in data.get("sources", [])}
print("yes" if target in ids else "no")
' "$SOURCE_ID")"

if [ "$source_exists" = "yes" ]; then
  echo "[seed] source '$SOURCE_ID' already present"
else
  echo "[seed] creating UA source '$SOURCE_ID' -> $SIM_ENDPOINT"
  curl -sf -X POST "$API/api/da/sources" -H 'Content-Type: application/json' -d "$(python3 -c '
import json, sys
print(json.dumps({
    "sourceId": sys.argv[1],
    "displayName": "UA Sim",
    "sourceType": "OpcUa",
    "endpointUrl": sys.argv[2],
    "securityMode": "None",
    "securityPolicy": "None",
    "maxMappedTags": 50000,
    "useSubscriptions": True,
    "updateRateMs": 1000,
}))
' "$SOURCE_ID" "$SIM_ENDPOINT")" >/dev/null
fi

# ---- 2. Build the tag payloads ------------------------------------------------
# Columns: itemId|displayName|dataType|unit|decimals|digital|mode|manualValue|onText|offText|influx|description
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

cat >"$TMP/rows.txt" <<'ROWS'
# ---- analog process values (live, from the UA simulation source) ----
ns=2;s=Tag00001|Tank 01 Level|Double|%|1||Source||||true|Tank farm A level transmitter
ns=2;s=Tag00002|Tank 01 Temperature|Double|°C|1||Source||||true|Tank farm A temperature
ns=2;s=Tag00003|Line 01 Pressure|Double|bar|2||Source||||true|Transfer line 01 discharge pressure
ns=2;s=Tag00004|Line 01 Flow|Double|m³/h|1||Source||||true|Transfer line 01 flow rate
ns=2;s=Tag00005|Pump 01 Speed|Double|rpm|0||Source||||true|Pump 01 drive speed
ns=2;s=Tag00006|Pump 01 Current|Double|A|1||Source||||true|Pump 01 motor current
ns=2;s=Tag00007|Motor 02 Speed|Double|rpm|0||Source||||true|Motor 02 drive speed
ns=2;s=Tag00008|Motor 02 Vibration|Double|mm/s|2||Source||||true|Motor 02 bearing vibration
ns=2;s=Tag00009|Compressor 01 Outlet Pressure|Double|bar|2||Source||||true|Compressor 01 discharge pressure
ns=2;s=Tag00010|Boiler 01 Steam Temperature|Double|°C|1||Source||||true|Boiler 01 main steam temperature
ns=2;s=Tag00011|Boiler 01 Drum Level|Double|%|1||Source||||true|Boiler 01 drum water level
ns=2;s=Tag00012|Line 02 Flow|Double|m³/h|1||Source||||true|Transfer line 02 flow rate
ns=2;s=Tag00013|Line 02 Pressure|Double|bar|2||Source||||true|Transfer line 02 discharge pressure
ns=2;s=Tag00014|Tank 02 Level|Double|%|1||Source||||true|Tank farm B level transmitter
ns=2;s=Tag00015|Tank 02 Temperature|Double|°C|1||Source||||true|Tank farm B temperature
ns=2;s=Tag00016|Reactor 01 Temperature|Double|°C|1||Source||||true|Reactor 01 jacket temperature
ns=2;s=Tag00017|Reactor 01 Pressure|Double|bar|2||Source||||true|Reactor 01 head pressure
ns=2;s=Tag00018|Cooler 01 Outlet Temperature|Double|°C|1||Source||||true|Cooler 01 outlet temperature
ns=2;s=Tag00019|Filter 01 Differential Pressure|Double|kPa|1||Source||||true|Filter 01 differential pressure
ns=2;s=Tag00020|Power 01 Total Demand|Double|kW|1||Source||||true|Plant total power demand
# ---- on/off and mode status (read from the sim's self-toggling Boolean nodes) ----
ns=2;s=Status/Pump01.Run|Pump 01 Running|Boolean|||true|Source||Running|Stopped|false|Pump 01 run status
ns=2;s=Status/Pump02.Run|Pump 02 Running|Boolean|||true|Source||Running|Stopped|false|Pump 02 run status
ns=2;s=Status/Motor02.Run|Motor 02 Running|Boolean|||true|Source||Running|Stopped|false|Motor 02 run status
ns=2;s=Status/Compressor01.Run|Compressor 01 Running|Boolean|||true|Source||Running|Stopped|false|Compressor 01 run status
ns=2;s=Status/Agitator01.Run|Reactor 01 Agitator Running|Boolean|||true|Source||Running|Stopped|false|Reactor 01 agitator run status
ns=2;s=Status/Valve01.Open|Valve 01 Position|Boolean|||true|Source||Open|Closed|false|Valve 01 open/closed
ns=2;s=Status/Valve02.Open|Valve 02 Position|Boolean|||true|Source||Open|Closed|false|Valve 02 open/closed
ns=2;s=Status/Alarm01.HighLevel|Tank 01 High Level Alarm|Boolean|||true|Source||ALARM|Normal|false|Tank 01 high level alarm
ns=2;s=Status/Mode01.Auto|Plant Control Mode|Boolean|||true|Source||Auto|Manual|false|Plant control mode select
ns=2;s=Status/Line01.Permit|Line 01 Start Permit|Boolean|||true|Source||Permit|Blocked|false|Line 01 start interlock permit
ROWS

tag_count="$(TAG_SOURCE_ID="$SOURCE_ID" ENABLE_INFLUX="$ENABLE_INFLUX" python3 - "$TMP" <<'PY'
import json
import os
import sys

tmp = sys.argv[1]
source_id = os.environ["TAG_SOURCE_ID"]
influx_default = os.environ["ENABLE_INFLUX"] == "1"

def opt(value):
    value = value.strip()
    return value or None

tags = []
with open(os.path.join(tmp, "rows.txt"), encoding="utf-8") as handle:
    for raw in handle:
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        cols = [c.strip() for c in line.split("|")]
        cols += [""] * (12 - len(cols))
        item, name, dtype, unit, dec, dig, mode, manual, on, off, influx, desc = cols[:12]
        tags.append({
            "sourceId": source_id,
            "itemId": item,
            "displayName": name,
            "description": opt(desc),
            "dataType": opt(dtype) or "Auto",
            "unit": opt(unit),
            "decimals": int(dec) if dec else None,
            "digital": True if dig.lower() == "true" else (False if dig.lower() == "false" else None),
            "mode": opt(mode) or "Source",
            "manualValue": opt(manual),
            "onText": opt(on),
            "offText": opt(off),
            "enabled": True,
            "accessRights": "Read",
            "trendStyle": "Continuous",
            "pollRateMs": 1000,
            "influxEnabled": influx_default and influx.lower() == "true",
        })

with open(os.path.join(tmp, "add.json"), "w", encoding="utf-8") as handle:
    json.dump({"tags": tags}, handle)

with open(os.path.join(tmp, "update.ndjson"), "w", encoding="utf-8") as handle:
    for tag in tags:
        handle.write(json.dumps({"tag": tag}) + "\n")

# Status tags were once seeded as static Manual values keyed by the bare name; collect
# those names so the shell can drop them.
legacy_prefix = "ns=2;s=Status/"
legacy = [t["itemId"][len(legacy_prefix):] for t in tags if t["itemId"].startswith(legacy_prefix)]
with open(os.path.join(tmp, "legacy.txt"), "w", encoding="utf-8") as handle:
    if legacy:
        # Trailing newline matters: `while read` drops a final line that has none.
        handle.write("\n".join(legacy) + "\n")

print(len(tags))
PY
)"

# /api/mappings/add is insert-only: this creates anything missing and skips the rest.
curl -sf -X POST "$API/api/mappings/bulk-add" \
  -H 'Content-Type: application/json' --data-binary @"$TMP/add.json" >/dev/null
echo "[seed] ensured $tag_count tags on source '$SOURCE_ID'"

# /api/mappings/update is a full replace, so this normalises every field on re-runs.
updated=0
while IFS= read -r payload; do
  [ -z "$payload" ] && continue
  curl -sf -X POST "$API/api/mappings/update" \
    -H 'Content-Type: application/json' --data-binary "$payload" >/dev/null
  updated=$((updated + 1))
done <"$TMP/update.ndjson"
echo "[seed] refreshed $updated tags"

# Drop status mappings from an older seed that used static Manual values.
if [ -s "$TMP/legacy.txt" ]; then
  legacy_pruned=0
  while IFS= read -r legacy_item; do
    [ -z "$legacy_item" ] && continue
    curl -s -X POST "$API/api/mappings/remove" -H 'Content-Type: application/json' \
      -d "{\"sourceId\":\"$SOURCE_ID\",\"itemId\":\"$legacy_item\"}" >/dev/null || true
    legacy_pruned=$((legacy_pruned + 1))
  done <"$TMP/legacy.txt"
  echo "[seed] dropped $legacy_pruned legacy manual status mappings (if any)"
fi

# ---- 3. Optionally drop the leftover demo mappings on the dead DA source ------
if [ "$PRUNE_STALE" = "1" ]; then
  for key in "default|DemoTag" "default|Pump.Run" "default|Valve.Open" "default|Level.PV"; do
    stale_source="${key%%|*}"
    stale_item="${key##*|}"
    curl -s -X POST "$API/api/mappings/remove" -H 'Content-Type: application/json' \
      -d "{\"sourceId\":\"$stale_source\",\"itemId\":\"$stale_item\"}" >/dev/null || true
    echo "[seed] pruned $stale_source/$stale_item"
  done
fi

# ---- 4. Summary -----------------------------------------------------------------
echo "[seed] waiting for the first poll cycle..."
sleep 3
echo "[seed] live values on '$SOURCE_ID':"
curl -sf -m 10 "$API/api/hmi/tags" | TAG_SOURCE_ID="$SOURCE_ID" python3 -c '
import json, os, sys

source = os.environ["TAG_SOURCE_ID"]
tags = [t for t in json.load(sys.stdin)["tags"] if t["sourceId"] == source]
live = [t for t in tags if t.get("value") is not None]
print("  {}/{} tags with a value".format(len(live), len(tags)))
for tag in tags[:8]:
    unit = " " + tag["unit"] if tag.get("unit") else ""
    value = tag.get("value")
    if tag.get("digital"):
        value = tag.get("onText") if value else tag.get("offText")
    print("  {:<40} {}{}".format(tag["displayName"], value, unit))
if len(tags) > 8:
    print("  ... and {} more".format(len(tags) - 8))
'
