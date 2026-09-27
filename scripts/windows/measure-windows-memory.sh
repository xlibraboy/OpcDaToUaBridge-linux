#!/usr/bin/env bash
# Sample a deployed OpcBridge bridge's memory on a Windows host, from Linux/WSL.
#
# Runs scripts/windows/monitor-opcbridge.ps1 over SSH, streams its verdict here and pulls
# the CSV back into dist/memory/. With --gc-mode it first flips the bridge's GC mode with
# set-bridge-gc-mode.ps1 (restart included), which is the A/B for issue #27 — the service's
# high working set on Windows.
#
# Usage:
#   scripts/windows/measure-windows-memory.sh --label baseline
#   scripts/windows/measure-windows-memory.sh --label workstation --gc-mode Workstation
#   scripts/windows/measure-windows-memory.sh --label server --gc-mode Server --minutes 20
#
# Options:
#   --host <ssh-alias>      SSH host or alias, default winvm-direct
#   --label <name>          names the local csv/log, default sample
#   --minutes <n>           sampling window, default 10
#   --interval <sec>        seconds between samples, default 10
#   --gc-mode <mode>        Server | Workstation - switch + restart before sampling
#   --warmup <sec>          wait after a switch before sampling, default 60 (0 without one)
#   --user <name>           dashboard sign-in so the app's own counters are included (optional)
#   --password <pass>       ... the pair is passed on the command line, so it is visible to
#                           process lists on the host (optional; omit for the plain sample)
#   --remote-dir <path>     Windows scratch dir, default C:/Windows/Temp/opcbridge-measure
#   --out-dir <path>        local results dir, default dist/memory (gitignored)
#
# Results: <out-dir>/<label>-<UTCstamp>.csv and .log. Compare the two .log files' Trend and
# Verdict blocks between arms — see docs/ram-measurement.md.
set -euo pipefail
cd "$(dirname "$0")/../.."

HOST=winvm-direct
LABEL=sample
MINUTES=10
INTERVAL=10
GC_MODE=''
WARMUP=''
BRIDGE_USER=''
BRIDGE_PASSWORD=''
REMOTE_DIR='C:/Windows/Temp/opcbridge-measure'
OUT_DIR=dist/memory

while [[ $# -gt 0 ]]; do
  case "$1" in
    --host) HOST="$2"; shift 2 ;;
    --label) LABEL="$2"; shift 2 ;;
    --minutes) MINUTES="$2"; shift 2 ;;
    --interval) INTERVAL="$2"; shift 2 ;;
    --gc-mode) GC_MODE="$2"; shift 2 ;;
    --warmup) WARMUP="$2"; shift 2 ;;
    --user) BRIDGE_USER="$2"; shift 2 ;;
    --password) BRIDGE_PASSWORD="$2"; shift 2 ;;
    --remote-dir) REMOTE_DIR="$2"; shift 2 ;;
    --out-dir) OUT_DIR="$2"; shift 2 ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $1 (try --help)" >&2; exit 2 ;;
  esac
done

case "$GC_MODE" in
  ''|Server|Workstation) ;;
  *) echo "--gc-mode must be Server or Workstation, got '$GC_MODE'" >&2; exit 2 ;;
esac

if [[ ! "$MINUTES" =~ ^[1-9][0-9]*$ ]]; then echo "--minutes must be a positive integer, got '$MINUTES'" >&2; exit 2; fi
if [[ ! "$INTERVAL" =~ ^[1-9][0-9]*$ ]]; then echo "--interval must be a positive integer, got '$INTERVAL'" >&2; exit 2; fi
if [[ -n "$WARMUP" && ! "$WARMUP" =~ ^[0-9]+$ ]]; then echo "--warmup must be seconds, got '$WARMUP'" >&2; exit 2; fi

if [[ -z "$WARMUP" ]]; then
  if [[ -n "$GC_MODE" ]]; then WARMUP=60; else WARMUP=0; fi
fi

LABEL="$(printf '%s' "$LABEL" | tr -cd 'A-Za-z0-9._-')"
if [[ -z "$LABEL" ]]; then LABEL=sample; fi

SAMPLES=$(( MINUTES * 60 / INTERVAL ))
if (( SAMPLES < 1 )); then SAMPLES=1; fi

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
REMOTE_WIN="${REMOTE_DIR//\//\\}"
REMOTE_CSV="$REMOTE_WIN\\$LABEL-$STAMP.csv"
LOCAL_BASE="$OUT_DIR/$LABEL-$STAMP"

mkdir -p "$OUT_DIR"

echo "==> Host $HOST, arm '$LABEL'${GC_MODE:+ (GC mode $GC_MODE)}, $SAMPLES samples x ${INTERVAL}s"
ssh "$HOST" "powershell -NoProfile -Command \"New-Item -ItemType Directory -Force -Path '$REMOTE_WIN' | Out-Null\""

echo "==> Uploading the measurement scripts"
scp -q scripts/windows/monitor-opcbridge.ps1 scripts/windows/set-bridge-gc-mode.ps1 "$HOST:$REMOTE_DIR/"

if [[ -n "$GC_MODE" ]]; then
  echo "==> Switching the bridge to $GC_MODE GC (this restarts it)"
  ssh "$HOST" "powershell -NoProfile -ExecutionPolicy Bypass -File \"$REMOTE_WIN\\set-bridge-gc-mode.ps1\" -Mode $GC_MODE"
fi

if (( WARMUP > 0 )); then
  echo "==> Warming up ${WARMUP}s so the restart's cold start stays out of the window"
  sleep "$WARMUP"
fi

CREDS=''
if [[ -n "$BRIDGE_USER" ]]; then
  CREDS=" -BridgeUser $BRIDGE_USER -BridgePassword $BRIDGE_PASSWORD"
fi

echo "==> Sampling $SAMPLES x ${INTERVAL}s (about $MINUTES min)"
# shellcheck disable=SC2029 # the remote command is meant to expand locally (paths, args)
ssh "$HOST" "powershell -NoProfile -ExecutionPolicy Bypass -File \"$REMOTE_WIN\\monitor-opcbridge.ps1\" -Samples $SAMPLES -IntervalSec $INTERVAL -OutFile \"$REMOTE_CSV\"$CREDS" | tee "$LOCAL_BASE.log"

echo "==> Fetching the CSV"
if ! scp -q "$HOST:$REMOTE_DIR/$LABEL-$STAMP.csv" "$LOCAL_BASE.csv"; then
  echo "!! Could not fetch $REMOTE_DIR/$LABEL-$STAMP.csv - the sampler output above says why." >&2
  exit 1
fi

echo ""
echo "==> Arm '$LABEL' done"
echo "    csv     : $LOCAL_BASE.csv"
echo "    verdict : $LOCAL_BASE.log"
if [[ -n "$GC_MODE" ]]; then
  if [[ "$GC_MODE" == 'Workstation' ]]; then OTHER=Server; else OTHER=Workstation; fi
  echo "    next    : scripts/windows/measure-windows-memory.sh --label $OTHER --gc-mode $OTHER --minutes $MINUTES"
else
  echo "    next    : run the other arm with --gc-mode Server|Workstation"
fi
echo "    compare : the Trend and Verdict blocks in the two .log files (docs/ram-measurement.md)"
