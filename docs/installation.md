# Installation on Windows

The bridge runs as a **background scheduled task** — no Windows Service, no admin required for daily operation. One-time setup needs admin for prerequisites only.

## Prerequisites (one-time, admin required on target PC)

1. **.NET 8 Runtime (x86)** — the bridge is a 32-bit app for COM alignment with OPC DA servers.
   - Download: https://dotnet.microsoft.com/download/dotnet/8.0
   - Install the **.NET Desktop Runtime (x86)** (includes ASP.NET for the dashboard)
   - Verify: open Command Prompt → `"C:\Program Files (x86)\dotnet\dotnet.exe" --info` → should show version 8.x
   - **Important:** Do NOT use the x64 runtime at `C:\Program Files\dotnet` — the bridge's `start-published-bridge.cmd` uses the x86 runtime at `C:\Program Files (x86)\dotnet\dotnet.exe`

2. **OPC DA server** — installed by the vendor (e.g. Matrikon OPC Simulation, Kepware, RSLinx). Verify it appears in `dcomcnfg` → Component Services → Computers → My Computer → DCOM Config.

3. **Windows Firewall** — open ports if accessing from other machines:
   - Port **8080/TCP** (default) — web dashboard; if the port was auto-assigned (Monitor → Bridge shows a different port), open that port instead
   - Port **4840/TCP** (default) — OPC UA server; same note applies if auto-assigned
   - Run as admin: `netsh advfirewall firewall add rule name="OPC Bridge Dashboard" dir=in action=allow protocol=TCP localport=8080` and `... localport=4840`
   - On first startup the bridge checks both ports; if either is already in use it silently moves to the next free port and saves it to `appsettings.json` (`Bridge:HttpPort`, `Bridge:OpcUaPort`). Check the Monitor tab or startup logs for the actual ports in use.

4. **DCOM permissions** (only for remote DA servers):
   - On the DA server host, run `dcomcnfg` → DCOM Config → find the OPC DA server → Properties → Security tab
   - Launch and Activation Permissions → add the user account → check **Remote Launch** + **Remote Activation**
   - Access Permissions → add the user → check **Remote Access**

## Install steps (no admin needed)

### Step 1 — Copy the app files

```
 Copy the publish folder to the target PC, e.g.:
   C:\OpcDaToUaBridge\publish\

 The folder must contain:
   OpcBridge.App.dll          ← main app
   OpcBridge.Da.dll            ← DA client library
   OpcBridge.Core.dll          ← shared types
   OpcBridge.Ua.dll            ← UA server library
   OpcBridge.App.deps.json     ← dependency manifest
   OpcBridge.App.runtimeconfig.json
   appsettings.json            ← config (edit for your DA server)
   *.dll (Opc.Ua.*, Microsoft.Extensions.*, etc.)

 Also copy the scripts folder:
   C:\OpcDaToUaBridge\scripts\windows\
     start-published-bridge.cmd       ← launcher (uses x86 dotnet)
     register-published-task.ps1      ← registers the scheduled task
     show-published-logs.ps1          ← reads task logs
```

### Step 2 — Edit appsettings.json

```json
{
  "Da": {
    "ProgId": "Matrikon.OPC.Simulation.1",   ← your OPC DA server ProgID
    "Host": "localhost",                       ← localhost or remote IP/hostname
    "UpdateRateMs": 1000,                      ← default poll rate
    "UseSubscriptions": true                   ← use IOPCDataCallback if supported
  },
  "Ua": {
    "ApplicationName": "OpcDaToUaBridge",
    "EndpointUrl": "opc.tcp://0.0.0.0:4840/OpcDaToUaBridge",
    "AutoAcceptUntrustedCertificates": true    ← set false for production
  },
  "Bridge": {
    "ExpectedTagCount": 1000,
    "RateLimits": { "100": 200, "500": 1000, "1000": 5000, ... }
  }
}
```

- **Da:ProgId** — find yours in `dcomcnfg` or the vendor's docs
- **Da:Host** — `localhost` for local DA server, or `192.168.x.x` / `hostname` for remote DCOM
- **Da:UseSubscriptions** — `true` = use callbacks (faster, supports deadband); `false` = polling only
- **Ua:AutoAcceptUntrustedCertificates** — `true` for testing; set `false` and manage certs in `pki/` for production

### Step 3 — Register the scheduled task

Open PowerShell (no admin needed) and run:

```powershell
cd C:\OpcDaToUaBridge
powershell -ExecutionPolicy Bypass -File scripts\windows\register-published-task.ps1
```

This creates a Windows Scheduled Task named **OpcDaToUaBridge** that:
- Starts automatically at **system startup** (not just logon)
- Runs as the current user with highest privileges
- Launches via `start-published-bridge.cmd` → `C:\Program Files (x86)\dotnet\dotnet.exe OpcBridge.App.dll`
- Redirects stdout/stderr to `publish\bridge-task-stdout.log` and `bridge-task-stderr.log`
- The script starts the task immediately and probes health for 20 seconds

### Step 4 — Verify

| Check | How | Expected |
|-------|-----|----------|
| Health | `http://localhost:8080/health` | `{"status":"ok"}` |
| Dashboard | Open `http://localhost:8080/` in browser | Dashboard loads, Monitor tab shows data |
| Bridge state | Monitor → Bridge | Running |
| DA connection | Monitor → DA | Connected |
| UA server | Monitor → UA | Running |
| Scheduled task | `schtasks /query /tn OpcDaToUaBridge` | State: Running |
| Version | Topbar badge or `http://localhost:8080/api/version` | e.g. v1.0.0 |

If DA shows Faulted, check:
- `appsettings.json` Da:ProgId is correct
- The DA server is running (check Windows Services)
- For remote: DCOM permissions on the remote host (see Prerequisites above)
- Logs: `http://localhost:8080/api/logs?limit=50` or run `scripts\windows\show-published-logs.ps1`

### Step 5 — Access from other machines

- Dashboard: `http://<windows-host-ip>:8080/`
- OPC UA: `opc.tcp://<windows-host-ip>:4840/OpcDaToUaBridge`
- Ensure Windows Firewall allows ports 8080 and 4840 (see Prerequisites)

## Files created at runtime

| File | Location | Purpose |
|------|----------|---------|
| `mappings.json` | `publish\` | Tag mappings (persists across restarts) |
| `sources.json` | `publish\` | DA source connections (persists across restarts) |
| `pki/` | `publish\pki\` | OPC UA certificates (own, trusted, rejected) |
| `bridge-task-stdout.log` | `publish\` | App stdout (info logs) |
| `bridge-task-stderr.log` | `publish\` | App stderr (errors, crashes) |

## Managing the scheduled task

```powershell
# Stop the bridge
schtasks /end /tn OpcDaToUaBridge

# Start the bridge
schtasks /run /tn OpcDaToUaBridge

# Check task state
schtasks /query /tn OpcDaToUaBridge /fo list

# View recent logs
powershell -File scripts\windows\show-published-logs.ps1

# Remove the task (uninstall)
schtasks /delete /tn OpcDaToUaBridge /f
```

## Backup and restore

Use the **Sources → OPC DA → Backup & Restore** section in the dashboard:
- **Export Config** — downloads a JSON file with all DA sources + tag mappings
- **Import Config** — restores from a previously exported file
- Passwords are NOT exported — re-enter DCOM credentials after import

Both `mappings.json` and `sources.json` can also be copied directly for backup.
