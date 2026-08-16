# Port Auto-Assignment Design

**Date:** 2026-08-01
**Status:** Approved

## Problem

The bridge hardcodes two listener ports:

- **HTTP:** `8080` — web dashboard + REST API
- **OPC UA:** `4840` — built-in UA server endpoint

If either is already in use on a new PC, the app fails with no recovery path. Operators have no way to know which port is in use or which port was auto-assigned.

## Solution

On first startup, the app scans for available ports. If defaults are taken, it picks the next free port and persists the chosen ports back to `appsettings.json`. Operators are informed via startup logs and the web dashboard.

## Config Changes

`appsettings.json` — add two new fields under `"Bridge"`:

```json
"Bridge": {
  "HttpPort": 8080,
  "OpcUaPort": 4840,
  ...
}
```

- Defaults remain `8080` / `4840`
- On first install (both values at default → default port is taken), the app auto-detects and overwrites
- Subsequent starts use the saved values

## Port Availability Checker

Static utility class in `OpcBridge.Core`:

```
PortHelper.IsPortAvailable(port) → bool       // TcpListener bind test
PortHelper.FindAvailablePort(start, end) → int  // scans upward, returns first free
```

Scan ranges:
- HTTP: `8080 → 8180`
- OPC UA: `4840 → 4940`

## Program.cs Startup Flow

```
1. Read HttpPort / OpcUaPort from config
2. Try bind HttpPort:
     - Success → use it
     - Fail    → FindAvailablePort(8080, 8180) → newPort
                 Log: "Port 8080 in use, auto-assigned to {newPort}. appsettings.json updated."
                 Write HttpPort = newPort to appsettings.json
3. Try bind OpcUaPort:
     - Success → use it
     - Fail    → FindAvailablePort(4840, 4940) → newPort
                 Log: "Port 4840 in use, auto-assigned to {newPort}. PKI cert will regenerate."
                 Write OpcUaPort = newPort to appsettings.json
                 Delete pki/own/cert.der (force cert regen with correct port)
4. UseUrls("http://0.0.0.0:{HttpPort}")
5. Set UaServerOptions.EndpointUrl = $"opc.tcp://0.0.0.0:{OpcUaPort}/OpcBridge"
6. Log chosen ports: "Bridge listening on http://0.0.0.0:{HttpPort}" and "OPC UA endpoint: opc.tcp://0.0.0.0:{OpcUaPort}/OpcBridge"
```

Config write only happens once (port collision), not on every startup.

## Startup Logs

```
[WRN] HttpPort 8080 already in use by another process. Auto-assigned to 8081. appsettings.json updated.
[WRN] OpcUaPort 4840 already in use. Auto-assigned to 4841. PKI certificate will be regenerated on next UA start.
[INF] Bridge listening on http://0.0.0.0:8081
[INF] OPC UA server endpoint: opc.tcp://0.0.0.0:4841/OpcBridge
```

## Monitor API Endpoint

`GET /api/status/ports` — returns runtime port info:

```json
{
  "httpPort": 8081,
  "uaPort": 4841,
  "httpDefault": 8080,
  "uaDefault": 4840,
  "httpAutoAssigned": true,
  "uaAutoAssigned": true
}
```

## Web Dashboard UI

### Banner (top of all pages)
Shown when HTTP port ≠ 8080:

```
⚠ Bridge is running on port 8081 (port 8080 was in use).
Update your HMI connect URL if needed. HMI: http://127.0.0.1:8081
```

Dismissible. Saved to sessionStorage so it doesn't re-appear on navigation.

### Monitor → Bridge Card — Ports Section

| Protocol | Status | Address | Note |
|----------|--------|---------|------|
| HTTP | ● | `http://192.168.20.13:8081` | (auto-assigned from 8080) |
| UA | ● | `opc.tcp://192.168.20.13:4841` | (auto-assigned from 4840) |

- `(auto-assigned from N)` badge only shown when port differs from default
- Tooltip: "This port was automatically changed from the default because the original port was in use by another application."
- Calls `GET /api/status/ports` on page load

## HMI (Avalonia) Port Discovery

`MainViewModel.cs` — replace hardcoded `http://127.0.0.1:8080` with:

```
1. Try GET /api/status/ports on http://127.0.0.1:8080
   - Success → use httpPort from response
   - Fail    → scan 8080→8180 sequentially:
                 Try GET /api/status/ports on each port
                 First success → use that port
2. Save discovered port to local appsettings.json / BridgePort field
3. Connect to discovered port
```

HMI `appsettings.json` — add default:

```json
"BridgePort": 8080
```

This is updated by HMI on successful discovery and persists across HMI restarts.

## Files to Change

### Core (shared)
| File | Change |
|------|--------|
| `OpcBridge.Core/PortHelper.cs` (new) | `IsPortAvailable`, `FindAvailablePort` |
| `OpcBridge.Core/BridgePorts.cs` (new) | DTO for `/api/status/ports` response |

### OpcBridge.App
| File | Change |
|------|--------|
| `Program.cs` | Port detection, config write, dynamic UseUrls + UaServerOptions |
| `BridgeState.cs` | Add `HttpPort`, `UaPort`, `HttpAutoAssigned`, `UaAutoAssigned` |
| `appsettings.json` | Add `HttpPort: 8080`, `OpcUaPort: 4840` |
| `HelpContent.cs` | Update port examples to use runtime values |

### Dashboard (OpcBridge.App/wwwroot/js)
| File | Change |
|------|--------|
| `DashboardPage.js` | Banner logic + Ports section in Monitor tab |
| `dashboard.css` | Banner styles |

### HMI (OpcBridge.Hmi)
| File | Change |
|------|--------|
| `ViewModels/MainViewModel.cs` | Auto-discovery on startup, use discovered port |
| `appsettings.json` | Add `BridgePort: 8080` |

### Documentation
| File | Change |
|------|--------|
| `scripts/windows/register-published-task.ps1` | Use runtime port from config instead of hardcoded 8080 |
| `scripts/windows/start-bridge-detached.cmd` | Already checks 8080; update to scan dynamically |

## Out of Scope

- MQTT / InfluxDB ports — external services, bridge connects *to* them, not hosts them
- Docker `EXPOSE` — handled at container run time via `-p` flag
- OPC UA port collision on the client side — external SCADA clients must update their connect strings (shown in Monitor tab)

## Acceptance Criteria

1. Bridge starts successfully on a PC where port 8080 is in use, using the next available port
2. Bridge starts successfully on a PC where port 4840 is in use, using the next available port
3. Both ports persisted to `appsettings.json` after auto-assignment
4. PKI certificate regenerated when UA port differs from default
5. Startup logs clearly indicate which ports were auto-assigned and why
6. Dashboard banner shown when HTTP port ≠ 8080
7. Monitor tab shows current ports with `(auto-assigned)` badge when applicable
8. HMI auto-discovers the bridge's HTTP port without manual configuration
9. All existing tests pass
10. Zero new compiler warnings
