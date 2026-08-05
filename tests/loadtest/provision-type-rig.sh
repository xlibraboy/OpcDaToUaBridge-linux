#!/usr/bin/env bash
# Provision the 3-source Live Values type verification rig against a bridge on :18082.
# Sims: 49321 (50k), 49322 (30k), 49323 (20k). Types: bulk Double + 3 manual demo tags
# (Int32 / Boolean / String) overwriting ua-a Tag00001..3.
set -euo pipefail

API="http://localhost:18082"
CHUNK=1000

add_source() {
  local id="$1" name="$2" port="$3" max="$4"
  curl -sf -X POST "$API/api/da/sources" -H 'Content-Type: application/json' -d "{
    \"sourceId\": \"$id\",
    \"displayName\": \"$name\",
    \"sourceType\": \"OpcUa\",
    \"endpointUrl\": \"opc.tcp://172.17.0.1:$port/opcuasim/\",
    \"securityMode\": \"None\",
    \"securityPolicy\": \"None\",
    \"maxMappedTags\": $max,
    \"useSubscriptions\": true,
    \"updateRateMs\": 1000
  }" >/dev/null
  echo "[type-rig] source $id created"
}

bulk_add() {
  local source="$1" start="$2" count="$3"
  local end=$((start + count - 1)) n payload
  payload='{"tags":['
  for ((n = start; n <= end; n++)); do
    [ "$n" -gt "$start" ] && payload+=','
    payload+="{\"sourceId\":\"$source\",\"itemId\":\"ns=2;s=Tag$(printf '%05d' $n)\",\"displayName\":\"Tag$(printf '%05d' $n)\",\"dataType\":\"Double\",\"pollRateMs\":1000}"
  done
  payload+=']}'
  curl -sf -X POST "$API/api/mappings/bulk-add" -H 'Content-Type: application/json' -d "$payload" >/dev/null
  echo "[type-rig] $count mappings for $source (Tag$(printf '%05d' $start)..Tag$(printf '%05d' $end))"
}

add_source ua-a "UA Sim A" 49321 50000
add_source ua-b "UA Sim B" 49322 30000
add_source ua-c "UA Sim C" 49323 20000

for ((start = 1; start <= 50000; start += CHUNK)); do bulk_add ua-a "$start" "$CHUNK"; done
for ((start = 1; start <= 30000; start += CHUNK)); do bulk_add ua-b "$start" "$CHUNK"; done
for ((start = 1; start <= 20000; start += CHUNK)); do bulk_add ua-c "$start" "$CHUNK"; done

echo "[type-rig] adding manual type-demo tags on ua-a (Tag00001..3)..."
curl -sf -X POST "$API/api/mappings/add" -H 'Content-Type: application/json' -d '{"tags":[
  {"sourceId":"ua-a","itemId":"ns=2;s=Tag00001","displayName":"Tag00001","dataType":"Int32","mode":"Manual","manualValue":"7","pollRateMs":1000},
  {"sourceId":"ua-a","itemId":"ns=2;s=Tag00002","displayName":"Tag00002","dataType":"Boolean","mode":"Manual","manualValue":"true","pollRateMs":1000},
  {"sourceId":"ua-a","itemId":"ns=2;s=Tag00003","displayName":"Tag00003","dataType":"String","mode":"Manual","manualValue":"hello","pollRateMs":1000}
]}' >/dev/null
echo "[type-rig] done"
