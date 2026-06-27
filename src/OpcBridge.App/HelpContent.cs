namespace OpcBridge.App;

internal static class HelpContent
{
    public const string Markdown = """
# Basic Workflow

- Use **Connection** to configure the OPC DA source, host, credentials, and polling rates.
- Use **Tags** to browse DA items, create DA → OPC UA mappings, and set per-tag poll rates.
- Use **Monitor** to confirm source reads, live values, rate-group alarms, and OPC UA writes.
- Use **Logs** to review warnings and errors from the bridge and UA server.
---

# Topology & Data Flow

```
  ┌─────────────────────────────────────────────────────────────────────┐
  │                        OPC DA Server (COM/DCOM)                      │
  │                   Matrikon / Kepware / RSLinx / etc.                │
  └──────────────┬──────────────────────────────┬───────────────────────┘
                 │                              │
          IOPCSyncIO.Read               IOPCItemMgt.AddItems
                 │                              │
  ┌──────────────▼──────────────────────────────▼───────────────────────┐
  │                      OpcDaClient (per source)                        │
  │                                                                      │
  │   ┌─── Rate Group 500ms ──┐  ┌─── Rate Group 1000ms ──┐  ┌── 5000ms │
  │   │ IOPCItemMgt + SyncIO  │  │ IOPCItemMgt + SyncIO   │  │  ...     │
  │   │ Tags: A, B, C         │  │ Tags: D, E             │  │  Tag: F  │
  │   └───────────┬───────────┘  └───────────┬────────────┘  └────┬─────┘
  │               │                          │                    │       │
  │         ReadAsync() ──────────────── merges all groups ───────┘       │
  └───────────────┬──────────────────────────────────────────────────────┘
                  │
          BridgeValue[] per poll cycle
                  │
  ┌───────────────▼──────────────────────────────────────────────────────┐
  │                      BridgeWorker (.NET 8)                            │
  │                                                                      │
  │   ┌─── Poller 500ms ────┐  ┌─── Poller 1000ms ───┐  ┌── Poller 5000ms│
  │   │ reads tags A,B,C    │  │ reads tags D,E      │  │  reads tag F   │
  │   │ Task.Delay(500)     │  │ Task.Delay(1000)    │  │  Task.Delay(5s)│
  │   └─────────┬───────────┘  └─────────┬──────────┘  └───────┬────────┘
  │             │                        │                     │         │
  │             └────────────┬───────────┴─────────────────────┘         │
  │                          │                                           │
  │          bridge_state_.SetValue() + ua_server_.UpdateValue()         │
  │          bridge_state_.UpdateRateGroup() ──► alarm bar                │
  └──────────────┬───────────────────────────────────────────────────────┘
                 │
          UpdateValue → ClearChangeMasks
                 │
  ┌──────────────▼───────────────────────────────────────────────────────┐
  │                   OPC UA Server (Opc.Ua.Server SDK)                   │
  │                                                                       │
  │   Folder: OpcDaTags (ns=2)                                            │
  │   ├── ns=2;s=sourceA/TagA   ← live value, quality, timestamp         │
  │   ├── ns=2;s=sourceA/TagB   ← live value                             │
  │   ├── ns=2;s=sourceA/TagD   ← live value                             │
  │   └── ns=2;s=sourceB/TagF   ← manual override value                  │
  └──────────────┬───────────────────────────────────────────────────────┘
                 │
          OPC UA TCP (opc.tcp://...)
                 │
  ┌──────────────▼─────────────────┐  ┌──────────────────────────────────┐
  │     UA Client 1 (HMI/SCADA)    │  │     UA Client 2 (Logger)          │
  │  Subscribes to monitored items │  │  Reads values on demand           │
  └────────────────────────────────┘  └──────────────────────────────────┘


  ┌─────────────────────────────────────────────────────────────────────┐
  │                      Web Dashboard (port 8080)                       │
  │                                                                      │
  │  Monitor ──► stats, source status, alarm bar, live values table      │
  │  Connection ──► source config, server browser, default rate          │
  │  Tags ──► DA browser, mappings, faceplate (per-tag rate/mode)        │
  │  Logs ──► recent warnings and errors                                 │
  │  Help ──► this page                                                  │
  │                                                                      │
  │  HTTP API: /api/dashboard, /api/mappings, /api/da/sources, etc.      │
  └─────────────────────────────────────────────────────────────────────┘
```

**Key data flow:**

- Each **source** has one `OpcDaClient` (one COM connection) with multiple **rate groups** (one OPC DA group per distinct rate).
- Each rate group has its own **poller task** that reads its tags independently at its own interval.
- Values flow: DA Server → OpcDaClient (COM read) → BridgeWorker (poller) → BridgeState (status) + UaServer (UA node update).
- UA clients subscribe to UA nodes and receive notifications when values change.
- The web dashboard reads from `/api/dashboard` (1s polling) to display live status.

## Unified UA Address Space

The bridge exposes **all tags from all DA sources** in a single OPC UA server address space. A connecting HMI/SCADA client sees one endpoint with all tags mixed together — it has no knowledge of how many DA servers exist behind the bridge.

### Counting example

```
 DA Side (multiple sources, multiple rate groups)

 Source A (localhost, Matrikon)
 ├── Rate 500ms:  2 tags  (Tag1, Tag2)
 ├── Rate 1000ms: 3 tags  (Tag3, Tag4, Tag5)
 └── Rate 5000ms: 1 tag   (Tag6)
                    ──────
                    6 tags total

 Source B (192.168.1.50, Kepware)
 ├── Rate 250ms:  10 tags (Tag7..Tag16)
 ├── Rate 1000ms: 5 tags  (Tag17..Tag21)
 └── Rate 5000ms: 4 tags  (Tag22..Tag25)
                    ──────
                    19 tags total

 Source C (192.168.1.60, RSLinx)
 └── Rate 1000ms: 15 tags (Tag26..Tag40)
                    ──────
                    15 tags total

 Total DA tags:  6 + 19 + 15 = 40 tags across 3 sources, 7 rate groups


 UA Side (one server, one address space)

 opc.tcp://bridge-host:4840/OpcDaToUaBridge
 Folder: OpcDaTags (ns=2)
 ├── ns=2;s=sourceA/Tag1    ← updated every 500ms by Source A poller
 ├── ns=2;s=sourceA/Tag2    ← updated every 500ms
 ├── ns=2;s=sourceA/Tag3    ← updated every 1000ms
 ├── ns=2;s=sourceA/Tag4    ← updated every 1000ms
 ├── ns=2;s=sourceA/Tag5    ← updated every 1000ms
 ├── ns=2;s=sourceA/Tag6    ← updated every 5000ms
 ├── ns=2;s=sourceB/Tag7    ← updated every 250ms
 ├──   ...                   ← (Tag8..Tag16 at 250ms)
 ├── ns=2;s=sourceB/Tag17   ← updated every 1000ms
 ├──   ...                   ← (Tag18..Tag21 at 1000ms)
 ├── ns=2;s=sourceB/Tag22   ← updated every 5000ms
 ├──   ...                   ← (Tag23..Tag25 at 5000ms)
 ├── ns=2;s=sourceC/Tag26   ← updated every 1000ms
 ├──   ...                   ← (Tag27..Tag40 at 1000ms)
 └── ns=2;s=sourceC/Tag40   ← updated every 1000ms

 Total UA nodes: 40 (one per DA tag mapping)
```

**The UA client subscribes to any subset of these 40 nodes.** It does not know (or need to know) that:
- The tags come from 3 different DA servers
- The tags are split across 7 OPC DA groups with different rates
- Tag1 updates 20× more often than Tag6

Each UA node simply reflects whatever value the DA-side poller last read. The client experiences them all as a single UA server with 40 variables.
---


# Poll Rate & Tag Limits

- Each tag can be assigned its own poll rate via the faceplate (Tags tab → click a tag). Tags with the same rate share one OPC DA group.
- Tags set to "Source Default" (poll rate = 0) inherit the global **Default Rate** (Connection tab).
- The global Default Rate is the single fallback for all tags without an explicit rate.
- Watch the alarm bar on the Monitor tab: <span class="good">green</span> = within limits, <span class="warn">yellow</span> = cycle budget warning, <span class="bad">red</span> = limit exceeded or saturated.

*(appsettings.json → `Bridge:RateLimits`)*

| Rate | Max Tags | Basis |
|------|----------|-------|
| 100 ms | 200 | COM device read ~0.4ms/item; 80ms budget at 80% cycle |
| 250 ms | 500 | Cached reads ~0.4ms; 200ms budget |
| 500 ms | 1,000 | Mixed cache/device ~0.4ms; 400ms budget |
| 1 s | 5,000 | Cached reads; 800ms budget; UA lock ~50ms |
| 2 s | 10,000 | ~1.6s budget; UA updates ~10K/sec |
| 5 s | 20,000 | ~4s budget; network ~2MB/s per UA client |
| 10 s | 50,000 | ~8s budget; network ~5MB/s; lock contention monitor |

## How limits are derived

- **DA COM read time** — `IOPCSyncIO.Read` with N items takes ~0.4–1ms/item (cache) or 2–5ms/item (device). Limit = 80% of rate interval ÷ per-item read time.
- **UA server lock** — each `UpdateValue` holds a lock for ~5–10μs. At 5000 tags × 500ms = 10K updates/sec, lock contention becomes measurable.
- **Network bandwidth** — each UA client notification is ~50–100 bytes. At 5000 tags × 500ms ≈ 500KB/s per client; 50K tags × 100ms ≈ 5MB/s saturates 100Mbps LAN.
- Limits are **conservative estimates**, not hard ceilings. Adjust in `appsettings.json` for your hardware and network. The alarm bar warns before degradation.

---

# Manual Override & Tag Modes

- **Source mode** — the tag publishes the live value read from the DA server (default).
- **Manual mode** — the tag publishes a fixed value you set, overriding the DA read. Switching to Manual with an empty field auto-copies the current live value.
- **Disabled** — the tag is not published to OPC UA and not read from DA.
- Open a tag's faceplate (Tags tab → click a tag) to change mode, set manual value, or adjust poll rate.

---

# OPC UA Server

- The bridge runs a built-in OPC UA server. UA clients connect to the endpoint shown on the Monitor tab.
- Each DA tag mapping creates one UA variable node under the "OPC DA Tags" folder (namespace index 2).
- Node IDs follow the pattern `ns=2;s={sourceId}/{daItemId}` unless a custom UA Node ID is specified.
- The UA server supports read and subscription (monitored items). Writes from UA clients are not supported (read-only bridge).

---

# OPC DA Server Discovery

The bridge can discover OPC DA servers installed on the **local machine** or on **remote hosts**. When credentials are provided, enumeration runs **under impersonation** so that servers registered in another user's profile are visible.

## How server registration works on Windows

```
 Machine-wide (HKLM)                Per-user (HKCU)
 ─────────────────────              ────────────────
 Installed by admin                 Installed by a user
 Visible to ALL users               Visible to THAT user only

 Registry path:                     Registry path:
 HKLM\SOFTWARE\Classes\CLSID        HKCU\SOFTWARE\Classes\CLSID
 HKLM\SOFTWARE\WOW6432Node\...      HKCU\SOFTWARE\WOW6432Node\...
```

## What the bridge scans

| Credentials provided | Local scan | Remote scan |
|----------------------|------------|-------------|
| **None** | HKLM only (machine-wide) | OpcEnum DCOM (no auth) |
| **Username + password** | HKLM + impersonated user's HKCU | OpcEnum under impersonation |

## Discovery workflow

```
 Connection tab → Credentials section

  Host: [localhost          ]
  User: [opcuser            ]  ← user who installed the OPC DA server
  Pass: [********           ]
  Domain: [WIN-PC           ]

  [Scan Servers]  ← click to enumerate

 Step 1: LogonUser("opcuser", "WIN-PC", "password", NEW_CREDENTIALS)
         → gets a Windows security token for opcuser

 Step 2: Under impersonation, scan:
         HKLM\SOFTWARE\Classes\CLSID          ← machine-wide servers
         HKCU\SOFTWARE\Classes\CLSID           ← opcuser's per-user servers

 Step 3: Results show BOTH:
         ✅ Matrikon.OPC.Simulation.1    (from HKLM — machine-wide)
         ✅ CustomOPC.Server.1            (from HKCU — opcuser's profile)

 Step 4: Select server → Save Source
         → same credentials used for COM connection (LogonUser + CreateInstance)
         → COM resolves ProgID from impersonated user's registry
         → server connects successfully
```

## When to use credentials for scanning

- **No credentials needed** — servers installed normally (machine-wide, admin install)
- **Credentials needed** — server installed by a specific user (per-user COM registration), or remote host requiring DCOM authentication
- **Manual ProgID** — if a server doesn't appear in scan, type the ProgID directly in the Connection tab's ProgID field and provide credentials

## Limitations

- The bridge scans only **one user's HKCU** per scan (the user whose credentials you provide)
- It does **not** scan all user profiles on the machine (that would require loading each user's NTUSER.DAT registry hive)
- Remote enumeration requires the `OpcEnum` service running on the target host and TCP port 135 (DCOM) accessible

---

# Troubleshooting

- **DA browse fails** — check ProgID, host reachability, DCOM permissions, and credentials (Connection tab → Credentials section).
- **Values stop moving** — check Monitor → Source Status for connection state and last read timing. Check the alarm bar for rate-group saturation.
- **Tags not appearing in UA** — verify the tag is Enabled and in Source mode (Tags tab → faceplate). Check Monitor → OPC UA Endpoint for node count.
- **Rate group saturated** — the read time exceeds 80% of the poll rate. Increase the rate or reduce the number of tags in that rate group.
- **Tag limit exceeded** — the number of tags in a rate group exceeds the configured limit. Move some tags to a slower rate or increase the limit in `appsettings.json`.

---

# Installation

The bridge app does **not require admin** — it installs to a per-user folder with a per-user scheduled task.

## Prerequisites (one-time, admin required on target PC)

- **OPC DA server** — installed by the vendor's installer (e.g. Matrikon, Kepware, RSLinx)
- **DCOM permissions** — configured for the DA server if remote access is needed (`dcomcnfg`)
- **Firewall ports** — port 8080 (dashboard) and 4840 (OPC UA) opened if accessed from other machines
- **.NET 8 runtime** — already installed on this machine; for a new target PC, either install the .NET 8 runtime or use a self-contained build (single `.exe`, no runtime needed)

## Install steps (no admin)

```
 1. Copy app folder to target PC
    e.g.  %LOCALAPPDATA%\OpcDaToUaBridge\
    Files:  OpcBridge.App.dll (or .exe for self-contained)
            appsettings.json
            scripts\windows\start-published-bridge.cmd

 2. Edit appsettings.json for the target machine
    Da:ProgId     → ProgID of the OPC DA server on this machine
    Da:Host       → localhost (or remote IP)
    Ua:EndpointUrl → opc.tcp://0.0.0.0:4840/OpcDaToUaBridge

 3. Register the scheduled task (no admin needed)
    Open Command Prompt, run:
    scripts\windows\register-published-task.ps1
    → creates task "OpcDaToUaBridge" for current user
    → task starts automatically on user logon

 4. Start the task
    schtasks /run /tn OpcDaToUaBridge

 5. Open dashboard in browser
    http://localhost:8080/
    → check Monitor tab: Bridge = Running, DA = Connected
```

## Verify installation

| Check | Expected |
|-------|----------|
| `http://localhost:8080/` | Dashboard loads |
| `http://localhost:8080/health` | `{"status":"ok"}` |
| `http://localhost:8080/api/version` | `{"version":"1.0.0.0", ...}` |
| Monitor → Bridge | Running |
| Monitor → DA Connection | Connected |

---

# Updating

Updates are **local only** — no internet required, no admin. New app files are copied from a USB drive or network share.

## Check current version

The version badge is shown in the topbar next to the app name (e.g. **v1.0.0**), or call `http://localhost:8080/api/version` to confirm.

## Update steps

```
 1. Receive new version files (USB drive, network share, etc.)
    New folder contains:
    OpcBridge.App.dll     ← updated app
    OpcBridge.Da.dll      ← updated DA library (if changed)
    OpcBridge.Core.dll    ← updated core library (if changed)
    appsettings.json      ← only if config schema changed
    update.cmd            ← optional update helper script

 2. Stop the running app
    schtasks /end /tn OpcDaToUaBridge

 3. Copy new files over the old ones
    copy /Y OpcBridge.App.dll  %LOCALAPPDATA%\OpcDaToUaBridge\
    copy /Y OpcBridge.Da.dll   %LOCALAPPDATA%\OpcDaToUaBridge\
    copy /Y OpcBridge.Core.dll %LOCALAPPDATA%\OpcDaToUaBridge\

 4. Restart the task
    schtasks /run /tn OpcDaToUaBridge

 5. Confirm the new version
    Check topbar badge → should show new version (e.g. v1.1.0)
    Check Monitor tab → Bridge = Running

 Note: appsettings.json and mappings.json are user data — do NOT
       overwrite them unless the config schema changed. Your source
       configs and tag mappings are preserved across updates.
```

## Using update.cmd (helper script)

If provided, `update.cmd` automates steps 2–5. Place it in the same folder as the new DLL files and double-click it:

```
 update.cmd (example)
 ─────────────────────────────────────────────────────
 @echo off
 schtasks /end /tn OpcDaToUaBridge
 ping -n 4 127.0.0.1 >nul
 copy /Y OpcBridge.App.dll  "%LOCALAPPDATA%\OpcDaToUaBridge\"
 copy /Y OpcBridge.Da.dll   "%LOCALAPPDATA%\OpcDaToUaBridge\"
 copy /Y OpcBridge.Core.dll "%LOCALAPPDATA%\OpcDaToUaBridge\"
 schtasks /run /tn OpcDaToUaBridge
 echo Update complete. New version is now running.
 pause
```

## What is preserved across updates

| File | Updated? | Notes |
|------|----------|-------|
| `OpcBridge.App.dll` | ✅ Yes | Main app binary |
| `OpcBridge.Da.dll` | ✅ Yes | DA client library |
| `OpcBridge.Core.dll` | ✅ Yes | Shared types |
| `appsettings.json` | ⚠️ Only if schema changed | Source configs, host, rates |
| `mappings.json` | ❌ Never overwrite | Your tag mappings |
| `pki/` | ❌ Never overwrite | OPC UA certificates |

---

# Configuration Reference

## appsettings.json

- **Da:ProgId** — OPC DA server ProgID (e.g. `Matrikon.OPC.Simulation.1`)
- **Da:Host** — DA server host (localhost or remote IP)
- **Da:UpdateRateMs** — default poll rate for new sources (min 100ms)
- **Ua:EndpointUrl** — OPC UA server endpoint (default `opc.tcp://0.0.0.0:4840/OpcDaToUaBridge`)
- **Ua:AutoAcceptUntrustedCertificates** — accept untrusted UA client certs (dev/test)
- **Bridge:RateLimits** — max tags per rate group (rate ms → max tags)
- **Bridge:Mappings** — initial tag mappings loaded at startup
""";
}
