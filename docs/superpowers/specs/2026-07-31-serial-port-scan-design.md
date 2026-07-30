# Serial Port Scan Design

- **Date:** 2026-07-31
- **Branch:** `bugfix/plc-s7200-driver` (worktree `.worktrees/bugfix-plc-s7200-driver`)
- **Status:** Approved — list-only scan
- **Surfaces:** Drivers form + Add Driver wizard; shared API for Melsec A3N and S7-200 PPI

## 1. Goal

Help operators pick the host serial device for PLC drivers without guessing `COMx` / `/dev/ttyUSB*`.

**Success:**
1. Click **Scan** next to the Port field.
2. See OS-reported serial ports.
3. Click **Use** (or pick from list) to fill the Port text box.
4. Still free-type a port if needed.
5. Existing **Test connection** unchanged (no handshake during Scan).

## 2. Decision

| Decision | Choice |
|---|---|
| Scan depth | **List only** — `SerialPort.GetPortNames()` |
| Free/busy open | Out of scope |
| PPI / A3N probe | Out of scope |
| Scope | Shared for **MelsecA3n** and **S7200Ppi** |
| UX | Port remains free-text; Scan fills it |

## 3. API

```http
GET /api/serial/ports
```

Response:

```json
{ "ports": ["COM3", "COM5"] }
```

Rules:
- Sorted ordinal (case-insensitive).
- Empty `ports` array when none (HTTP 200).
- Enumeration exceptions → HTTP 200 with `ports: []` and optional `error` string (UI still usable).
- No open, no write, no protocol.

## 4. UI

### Drivers form (`drvA3nPort`)
- Port row: text input + **Scan** (`btnDrvScanPorts`).
- List under row (`listDrvPorts`) with **Use** per port (same pattern as UA discover).
- Status span (`msgDrvPorts`).

### Wizard step 3 (`wzDrvPort`)
- Same: **Scan** (`btnWzDrvScanPorts`), list (`listWzDrvPorts`), message (`msgWzDrvPorts`).

### Behavior
1. Scan → `GET /api/serial/ports` (`cache: 'no-store'`).
2. Render ports; **Use** writes into the matching Port input.
3. Message: `N port(s) found` / `No serial ports found` / error text.

## 5. Tests

- API: `GET /api/serial/ports` returns 200 and `ports` array.
- Dashboard: HTML has scan controls; Script calls `/api/serial/ports` and `scanSerialPorts`.

## 6. Non-goals

- Auto-select “the PPI cable”.
- Docker/WSL USB passthrough.
- Refresh-on-plug hotplug without Scan click.
