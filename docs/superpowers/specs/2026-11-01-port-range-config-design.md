# Per-Install Port Range Design

**Date:** 2026-11-01
**Status:** Implemented

## Problem

The auto-assignment ranges were hardcoded constants: HTTP `8080-8180`, OPC UA `4840-4940`
(`PortHelper.HttpScanStart/End`, `OpcUaScanStart/End`). Every install shared them, so two
installs on one machine — or an install beside another product that already owns 8080 — fell
back into the same block. The Logic page is a route on the bridge (`/logic`), not a separate
app, so it always appeared on the bridge's port and could not be moved independently.

## Solution

Each install owns its blocks, configured in the data directory's `appsettings.json`:

```json
"Bridge": {
  "HttpPort": 8080,
  "OpcUaPort": 4840,
  "HttpPortRange": "8080-8180",
  "OpcUaPortRange": "4840-4940"
}
```

- `HttpPort` / `OpcUaPort` remain the preferred ports.
- `*PortRange` is the block scanned when a preferred port is taken.
- Missing keys keep the old defaults, so existing installs behave exactly as before.
- A malformed value falls back to the default and is logged / reported, never fatal.
- Overlapping blocks are reported; startup still honours them but the UA scan skips the port
  HTTP took, so both listeners can never bind one number.
- A preferred port outside its block is still honoured; the block decides fallbacks only.

## Core

`PortRange` (`src/OpcBridge.Core/PortRange.cs`, new): `Start`, `End`, `IsValid`, `Contains`,
`Overlaps`, `Length`, `TryParse("start-end")`, `ToString()`. `PortHelper.FindAvailablePort`
takes a `PortRange` plus an optional excluded port. `PortHelper.ScanRangeFromEnvironment`
lets clients follow a custom block via `OPCBRIDGE_HTTP_SCAN_RANGE`.

## Startup

`Program.cs` reads the blocks and the preferred ports, uses a preferred port when free, else
scans that protocol's block (UA excluding HTTP), then persists the chosen ports and blocks
when they changed. Auto-assigned means "running port differs from the preferred port".

## API

- `GET /api/status/ports`: `httpRange` / `uaRange` added; `httpDefault` / `uaDefault` report
  the preferred ports (so the auto-assigned wording stays truthful).
- `GET /api/ports/config`: `scanRanges` is the running block, `savedRanges` the next-start
  block, `rangeProblems` the startup validation messages; `restartRequired` covers ranges.
- `POST /api/ports/config`: optional `httpRange` / `uaRange` objects (`{start,end}`);
  invalid or overlapping pairs are rejected with 400.
- `POST /api/ports/probe`: suggestions scan the configured block and echo it as `range`.

## Dashboard and clients

Monitor → Port Configuration gains start/end inputs for both blocks beside the existing port
fields; the save sends them with the ports. The desktop HMI/Designer and the phone sweep the
default block unless `OPCBRIDGE_HTTP_SCAN_RANGE` is set; a bridge outside the default block
is still reachable by the URL the operator configures.

## Out of scope

- Multiple bridge instances on one machine (the single-instance lock still refuses a second).
- A separate listener/origin for the Logic page — it stays same-origin with `/api/logic` and
  the `/hmi` hub (`context.md`).
