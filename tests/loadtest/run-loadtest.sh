#!/usr/bin/env bash
# Load-test orchestrator for the OPC UA source path.
#
# Topology:
#   opcua-sim-20k   (OpcUaSimServer, N nodes)   host port $SIM_HOST_PORT  -> 4840
#   opcbridge-lt    (bridge built from worktree) host port $HTTP_HOST_PORT -> 8080, $UA_HOST_PORT -> 4840
#   bridge UA source -> opc.tcp://172.17.0.1:$SIM_HOST_PORT/opcuasim/  (172.17.0.1 = docker gateway)
#
# Env overrides:
#   NODES          tag count to simulate AND map (default 20000)
#   SIM_HOST_PORT  (default 49321)
#   HTTP_HOST_PORT (default 18081)
#   UA_HOST_PORT   (default 4841)
#   UPDATE_MS      sim update cadence (default 1000)
#   WRITEABLE      sim nodes that accept UA writes (default 10; written nodes freeze)
#   BAD_TAGS       comma list of 1-based tag numbers fault-injected BadOutOfService (default none)
#   BAD_AFTER_MS   delay before bad tags flip (default 0 = first tick)
set -euo pipefail

NODES="${NODES:-20000}"
SIM_HOST_PORT="${SIM_HOST_PORT:-49321}"
HTTP_HOST_PORT="${HTTP_HOST_PORT:-18081}"
UA_HOST_PORT="${UA_HOST_PORT:-4841}"
UPDATE_MS="${UPDATE_MS:-1000}"
WRITEABLE="${WRITEABLE:-10}"
BAD_TAGS="${BAD_TAGS:-}"
BAD_AFTER_MS="${BAD_AFTER_MS:-0}"
BRIDGE_IMG="${BRIDGE_IMG:-opcbridge:loadtest}"
SIM_IMG="${SIM_IMG:-opcuasim:loadtest}"
WORKTREE="$(cd "$(dirname "$0")/../.." && pwd)"
SIM_ENDPOINT="opc.tcp://172.17.0.1:${SIM_HOST_PORT}/opcuasim/"
API="http://localhost:${HTTP_HOST_PORT}"
SOURCE_ID="ua-loadtest"

log() { echo "[loadtest] $*"; }

cleanup() {
  docker rm -f opcbridge-lt >/dev/null 2>&1 || true
  docker rm -f opcua-sim-20k >/dev/null 2>&1 || true
}
trap cleanup EXIT

log "worktree: $WORKTREE"

log "building sim image ($SIM_IMG)..."
docker build -f "$WORKTREE/tests/loadtest/opcuasim.Dockerfile" -t "$SIM_IMG" "$WORKTREE"

log "building bridge image ($BRIDGE_IMG) from Dockerfile.local..."
docker build -f "$WORKTREE/Dockerfile.local" -t "$BRIDGE_IMG" "$WORKTREE"

log "starting sim container (NODES=$NODES, WRITEABLE=$WRITEABLE, BAD_TAGS=$BAD_TAGS)..."
docker run -d --name opcua-sim-20k \
  -e SIM_NODES="$NODES" -e SIM_UPDATE_MS="$UPDATE_MS" -e SIM_WRITEABLE="$WRITEABLE" \
  -e SIM_BAD_TAGS="$BAD_TAGS" -e SIM_BAD_AFTER_MS="$BAD_AFTER_MS" \
  -p "$SIM_HOST_PORT":4840 "$SIM_IMG" >/dev/null

log "starting bridge container (http $HTTP_HOST_PORT, ua $UA_HOST_PORT)..."
docker run -d --name opcbridge-lt \
  -e Bridge__ExpectedTagCount="$NODES" \
  -e Bridge__RateLimits__100="$NODES" \
  -e Bridge__RateLimits__250="$NODES" \
  -e Bridge__RateLimits__500="$NODES" \
  -e Bridge__RateLimits__1000="$NODES" \
  -e Bridge__RateLimits__2000="$NODES" \
  -e Bridge__RateLimits__5000="$NODES" \
  -e Bridge__RateLimits__10000="$NODES" \
  -p "$HTTP_HOST_PORT":8080 -p "$UA_HOST_PORT":4840 "$BRIDGE_IMG" >/dev/null

log "waiting for bridge API..."
for _ in $(seq 1 60); do
  curl -sf "$API/api/status" >/dev/null 2>&1 && break
  sleep 1
done
curl -sf "$API/api/status" >/dev/null || { log "bridge API never came up"; exit 1; }

log "testing UA connection to sim..."
curl -sf -X POST "$API/api/ua/test-connection" \
  -H 'Content-Type: application/json' \
  -d "{\"endpointUrl\":\"$SIM_ENDPOINT\",\"securityMode\":\"None\",\"securityPolicy\":\"None\"}" \
  | jq -c .; echo

log "creating UA source '$SOURCE_ID'..."
curl -sf -X POST "$API/api/da/sources" -H 'Content-Type: application/json' -d "{
  \"sourceId\": \"$SOURCE_ID\",
  \"displayName\": \"UA LoadTest\",
  \"sourceType\": \"OpcUa\",
  \"endpointUrl\": \"$SIM_ENDPOINT\",
  \"securityMode\": \"None\",
  \"securityPolicy\": \"None\",
  \"maxMappedTags\": 100000,
  \"useSubscriptions\": true,
  \"updateRateMs\": 1000
}" | jq -c .; echo

log "bulk-adding $NODES mappings in chunks of 1000..."
CHUNK=1000
total_received=0
for ((start = 1; start <= NODES; start += CHUNK)); do
  end=$((start + CHUNK - 1))
  [ "$end" -gt "$NODES" ] && end=$NODES
  tags="["
  for ((i = start; i <= end; i++)); do
    [ "$i" -gt "$start" ] && tags+=","
    name=$(printf 'Tag%05d' "$i")
    tags+="{\"sourceId\":\"$SOURCE_ID\",\"itemId\":\"ns=2;s=$name\",\"displayName\":\"$name\",\"dataType\":\"Double\",\"pollRateMs\":1000}"
  done
  tags+="]"
  resp=$(curl -sf -X POST "$API/api/mappings/bulk-add" -H 'Content-Type: application/json' -d "{\"tags\":$tags}")
  received=$(printf '%s' "$resp" | jq -r '.received')
  total_received=$((total_received + received))
  log "  chunk $start-$end: received=$received (total $total_received)"
done

log "waiting for bridge to connect + subscribe..."
sleep 10
for _ in $(seq 1 30); do
  status=$(curl -sf "$API/api/status")
  conn=$(printf '%s' "$status" | jq -r '.bridge.sources[0].connectionState // "n/a"')
  mappings=$(printf '%s' "$status" | jq -r '.bridge.mappingCount')
  rate=$(printf '%s' "$status" | jq -r '.bridge.lastPollValueRate // "n/a"')
  log "  poll: source=$conn mappings=$mappings valueRate=$rate"
  sleep 2
done

log "--- final status ---"
curl -sf "$API/api/status" | jq -c . | head -c 2500; echo
log "--- diagnostics ---"
curl -sf "$API/api/diagnostics" | jq -c . | head -c 2500; echo
log "--- container stats ---"
docker stats --no-stream --format 'table {{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}' opcua-sim-20k opcbridge-lt
