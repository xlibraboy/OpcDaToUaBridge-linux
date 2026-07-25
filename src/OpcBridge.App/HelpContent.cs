namespace OpcBridge.App;

internal static class HelpContent
{
    public const string Markdown = """
# Getting Started

## Basic Workflow

- Use **Connection** to configure server connections (OPC DA address, credentials, default update rate, and DA subscriptions toggle).
- Use **Tags** to browse DA items, create DA → OPC UA mappings, and set per-tag Access Rights, Update Rate, Deadband, Description, and Simulation.
- Use **OPC DA to DA** to connect tags — a consumer tag receives values from a provider tag in addition to its own DA source.
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
│  Connection ──► server connection config, discovery, default rate, subscriptions toggle  │
│  Tags ──► DA browser, mappings, faceplate (access rights, update rate, simulation)  │
│  OPC DA to DA ──► connected tags: set/clear provider→consumer links, view all connections   │
  │  Help ──► this page                                                  │
  │                                                                      │
   │  HTTP API: /api/dashboard, /api/mappings, /api/da/sources, etc.      │
   │                                                                      │
   │  **Apps Pill**: Shows count of detected bridge instances across all    │
   │  configured DA source hosts. Updates every 10 seconds.                 │
  └─────────────────────────────────────────────────────────────────────┘
```

**Key data flow:**

- Each **source** has one `OpcDaClient` (one COM connection) pinned to a **dedicated STA thread**, with multiple **rate groups** (one OPC DA group per distinct rate).
- Values arrive either via **subscription callbacks** (`IOPCDataCallback`, default) or **poller tasks** (`IOPCSyncIO.Read`, fallback) — one path per rate group.
- Subscription values flow: DA Server → `IOPCDataCallback` → OpcDaClient → BridgeWorker → BridgeState + UaServer. Poll values flow: DA Server → `IOPCSyncIO.Read` → poller task → BridgeState + UaServer.
- **UA writes** (writeable mappings) flow: UA Client → BridgeNodeManager → WriteQueue → per-source consumer → `IOPCSyncIO.Write` → DA Server.
- UA clients subscribe to UA nodes and receive notifications when values change.
- The web dashboard reads from `/api/dashboard` (1s polling) to display live status and resource telemetry.

## OPC UA Endpoint — Bind vs Connect

The **Endpoint URL** in Connection settings has two faces:

| Field | Value | Purpose |
|---|---|---|
| **Endpoint (config)** | `opc.tcp://0.0.0.0:4840/OpcBridge` | The server's **bind address**. `0.0.0.0` means "listen on all network interfaces" (localhost + LAN + VPN). This is the correct value for a server. |
| **Connect from client** | `opc.tcp://<hostname>:4840/OpcBridge` | The URL you enter in an **OPC UA client** to connect. The dashboard shows this with the host's real name filled in. |

**Do not** put `0.0.0.0` in your client's connect string — `0.0.0.0` means "this machine" to a client, which is the *client's* own machine, not the bridge. Always use the bridge host's IP address or hostname:

- Same machine: `opc.tcp://localhost:4840/OpcBridge`
- Another machine on the LAN: `opc.tcp://192.168.x.x:4840/OpcBridge` or `opc.tcp://HOSTNAME:4840/OpcBridge`

The **Monitor** tab shows both values: the configured bind address and the derived client connect URL.

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

opc.tcp://bridge-host:4840/OpcBridge
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

===

# Update Rate & Tag Limits

- Each tag can be assigned its own update rate via the faceplate (Tags tab → click a tag → Setup tab). Tags with the same rate share one OPC DA group.
- Tags set to "Source Default" (update rate = 0) inherit the global **Default Update Rate** (Connection tab).
- The global Default Update Rate is the single fallback for all tags without an explicit rate.
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


# Access Rights & Simulation

Each tag has two independent settings: **Access Rights** (data flow direction) and **Simulation** (manual value injection).

## Access Rights

Set via the faceplate → **Setup** tab:

| Access Right | Data Flow | Description |
|---|---|---|
| **Read** | DA → UA | Bridge reads from the DA server and publishes to UA. UA clients can only observe. |
| **Read-Write** | DA ↔ UA | Bridge reads from DA AND UA client writes flow back to DA via `IOPCSyncIO.Write`. |
| **Write** | UA → DA | UA clients write values that the bridge pushes to DA. No DA polling, no UA publishing. UA node is write-only (`AccessLevel = CurrentWrite`). |

## Simulation

Set via the faceplate → **Simulation** tab. Independent of Access Rights:

- **OFF** (default) — the tag reads from DA (for Read/Read-Write) or accepts UA writes (for Write).
- **ON** — the bridge publishes a fixed **Manual Value** to UA instead of reading from DA. Use this to inject test values or simulate a DA source.

| Access Rights | Simulation | Behavior |
|---|---|---|
| Read | OFF | Poll DA → publish to UA |
| Read | ON | Publish Manual Value to UA (no DA read) |
| Read-Write | OFF | Poll DA → publish + accept UA→DA writes |
| Read-Write | ON | Publish Manual Value + accept UA→DA writes |
| Write | OFF | No DA poll, no UA publish, accept UA→DA writes |
| Write | ON | Publish Manual Value + accept UA→DA writes |

## Disabled Tags

- A tag can be **Disabled** (Setup tab → Enabled checkbox). Disabled tags are not read from DA and not published to UA.
- Open a tag's faceplate (Tags tab → click a tag) to change access rights, simulation, update rate, or description.

---

# DA Links

DA Links are a **separate subsystem** from DA → UA mappings. A provider change on one OPC DA source can write directly to a consumer on another OPC DA source through the bridge's shared DA runtime without changing the mapping payload for that consumer.

## How it works

- The **provider** tag is read from its DA source normally and must have Access Rights that include **Read**.
- The **consumer** tag keeps its own mapping and must have Access Rights that include **Write** or **Read-Write** so the bridge can forward provider changes into its DA server.
- DA Links share the bridge runtime with mappings, so cross-source forwarding works even when the provider and consumer live on different OPC DA servers.
- Runtime forwarding is driven by stored `DaLinkRule` entries. Legacy `providerSourceId` / `providerDaItemId` fields exist only for migration from older mapping files.

## Setting up links

1. Open the **OPC DA to DA** tab.
2. Pick a **Consumer** tag.
3. Pick a **Provider** tag.
4. Click **Save Link**.
5. The link appears in the Links list and can be removed with **Delete Link**.

## Rules

- A tag cannot link to itself.
- Cross-source links are supported.
- Provider and consumer must use the same canonical OPC DA type.
- v1 allows only **one provider per consumer**.
- Clearing a DA Link stops forwarding immediately and leaves the DA → UA mapping unchanged.

## Runtime flow

```
  DA Server A                          DA Server B
      │                                    │
  Provider Tag                         Consumer Tag
      │                                    │
      ▼                                    ▼
  BridgeWorker poll/subscription      BridgeWorker mapping/runtime state
      │                                    │
      └── DaLinkRule match ───────────────► WriteQueue(B) ──► IOPCSyncIO.Write
```

---

# OPC UA Server

- The bridge runs a built-in OPC UA server. UA clients connect to the endpoint shown on the Monitor tab.
- Each DA tag mapping creates one UA variable node under the "OPC DA Tags" folder (namespace index 2).
- Node IDs follow the pattern `ns=2;s={sourceId}/{daItemId}` unless a custom UA Node ID is specified.
- The UA server supports read, subscription (monitored items), and **writes** for tags with Read-Write or Write access rights.

## UA Writes (UA → DA passthrough)

When a tag's Access Rights is **Read-Write** or **Write**:

- The UA variable's `AccessLevel` includes `CurrentWrite`, so UA clients can write to it.
- A write from any UA client drains through a bounded queue (capacity 1024) to `IOPCSyncIO.Write` on the DA server.
- One consumer per DA source keeps all COM write work on that source's dedicated STA thread.
- If the write succeeds, the UA value is accepted; on failure the UA write is rejected with `BadNoCommunication` or `BadRequestTimeout` (5s).
- **Read** access rights remain read-only (`AccessLevel = CurrentRead` only).

```
  UA Client writes value ─► BridgeNodeManager.OnWriteValue
                                 │
                          TaskCompletionSource<bool>
                                 │
                          WriteQueue (bounded channel, 1024)
                                 │
                   per-source consumer task
                                 │
                   OpcDaClient.WriteAsync (STA thread)
                                 │
                          IOPCSyncIO.Write
                                 │
                           DA Server
```

---

# MQTT (OPC UA ↔ External Broker)

## Important: You Need an External MQTT Broker

**This app does NOT include its own MQTT broker.** It connects TO an external MQTT broker (like Mosquitto, HiveMQ, EMQX, AWS IoT, etc.). You must have a broker running before you can use MQTT features.

**Quick start with Mosquitto (Docker):**
```bash
docker run -d -p 1883:1883 --name mosquitto eclipse-mosquitto
```

Then configure the bridge to connect to `tcp://localhost:1883`.

## What MQTT Does

The bridge can:
- **Publish** OPC UA tag values to your MQTT broker (when tags have MQTT enabled)
- **Subscribe** to topics and write inbound messages back through the OPC UA path

MQTT is scoped to the **OPC UA layer** — it reads the mirrored UA tag values and writes through the same UA write path a UA client uses.

## Setup Steps

1. **Start an MQTT broker** (see examples above)
2. **Configure the broker connection** in the MQTT Broker section:
   - Turn ON the **Enabled** checkbox
   - Enter your broker URL (e.g., `tcp://localhost:1883`)
   - Add credentials if your broker requires them
   - Click **Save Config** then **Connect**
3. **Enable MQTT for specific tags**:
   - Go to Tags tab → click a tag → check the **MQTT** checkbox
   - Optionally set a custom topic in the **MQTT Topic** field

## Topics

- **Publish**: `{TopicPrefix}/{SourceId}/{DaItemId}` (default prefix `bridge/tags`), or a per-tag `MqttTopic` override set in the tag faceplate.
- **Subscribe**: the bridge subscribes to `{TopicPrefix}/#` and resolves inbound topics to tags the same way.

## Payload

Minimal JSON. Selectable fields (MQTT Broker → Payload Fields): `v` (value), `t` (timestamp), `q` (quality), `sourceId`, `itemId`, `displayName`, `dataType`. Default is `v` + `t`.

```json
{ "v": 12.3, "t": "2026-07-08T12:00:00.0000000Z" }
```

## Notes

- **Connection resilience**: Publish and subscribe are failure-resilient — a broker outage does not stop the bridge; the client auto-reconnects.
- **Write protection**: Inbound writes to a tag flow through the UA write path (same as a UA client write) and are rejected if the tag is read-only.
- **Popular MQTT brokers**: Mosquitto (free, open source), HiveMQ (enterprise), EMQX (scalable), AWS IoT Core, Azure IoT Hub, Google Cloud IoT Core.

## Subscriptions & Deadband

- Subscriptions can be toggled in **Connection tab → DA Subscriptions**. When ON (default), the bridge uses `IOPCDataCallback` to receive value changes from the DA server instead of polling with `IOPCSyncIO.Read`. Changing the toggle takes effect on the next source reconnect.
- Subscriptions deliver values on change (faster than update rate) and respect the per-group **deadband**.
- **Deadband %** (Tags tab → faceplate → Setup tab → Deadband %) sets the OPC DA group's `percentDeadband`. The DA server suppresses callbacks for changes within the deadband. Set 0 for no filtering, 1.0 for 1% noise suppression.
- If the DA server does not support `IOPCDataCallback`, the bridge logs a warning and falls back to device-read polling — deadband has no effect in polling mode.
- All COM work for a source (reads, writes, subscription callbacks) is pinned to a dedicated **STA thread** per source to avoid COM apartment marshalling failures.

## Resource Telemetry

- The Monitor tab shows a **Resources** panel with native process counters (Windows only):
  - **Handles** — total OS handles held by the process (`GetProcessHandleCount`)
  - **GDI / USER** — GDI and USER object counts (`GetGuiResources`)
- These are sampled every 5 seconds. A steady or slowly-growing handle count confirms no COM/resource leak. On non-Windows, the panel shows "n/a (non-Windows)".
- Watch for handle count growth over time — a steady upward trend indicates a handle or COM object leak.

---

# InfluxDB (Historical Logging)

- External InfluxDB 2.x/3.x server required
- Configure URL, Org, Bucket, Token on InfluxDB tab; Save + Connect
- Enable per tag via faceplate Influx checkbox
- Points: measurement opc_tags (configurable), tags source_id/da_item_id/display_name, fields value/quality/is_good
- Outage does not stop the bridge

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

 Step 4: Select server → Save Connection
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
- **Tags not appearing in UA** — verify the tag is Enabled (Tags tab → faceplate → Setup tab). Check Monitor → OPC UA Endpoint for node count.
- **Rate group saturated** — the read time exceeds 80% of the update rate. Increase the rate or reduce the number of tags in that rate group.
- **Tag limit exceeded** — the number of tags in a rate group exceeds the configured limit. Move some tags to a slower rate or increase the limit in `appsettings.json`.
- **UA write rejected** — verify the tag's Access Rights is **Read-Write** or **Write** (Tags tab → faceplate → Setup tab). Read-only tags return `BadWriteNotSupported`. A write that times out (DA server unresponsive for 5s) returns `BadRequestTimeout`; a DA-side failure returns `BadNoCommunication`.
- **Deadband not filtering** — deadband only works under **subscriptions** (Connection tab → DA Subscriptions ON). If the DA server doesn't support `IOPCDataCallback`, the bridge falls back to polling and deadband has no effect. Check Logs for the subscription fallback warning.
- **Handle count growing** — a steady upward trend in Monitor → Resources → Handles indicates a COM object or handle leak. Restart the scheduled task if it grows unbounded; report the source for investigation.
- **Subscription fallback** — if `/api/logs` shows "OPC DA server does not support subscriptions", the bridge silently switched to polling. Values still flow at the update rate. This is non-fatal.

---

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
   - Port **8080/TCP** — web dashboard
   - Port **4840/TCP** — OPC UA server
   - Run as admin: `netsh advfirewall firewall add rule name="OPC Bridge Dashboard" dir=in action=allow protocol=TCP localport=8080` and `... localport=4840`

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

Use the **Connection tab → Backup & Restore** section in the dashboard:
- **Export Config** — downloads a JSON file with all DA sources + tag mappings
- **Import Config** — restores from a previously exported file
- Passwords are NOT exported — re-enter DCOM credentials after import

Both `mappings.json` and `sources.json` can also be copied directly for backup.
---

# Updating to a New Version

Updates are **local only** — no internet, no admin. Overwrite the DLLs and restart. Your config and tag mappings are preserved.

## Before you update

1. **Check current version** — look at the topbar badge (e.g. **v1.0.0**) or call `http://localhost:8080/api/version`
2. **Export a backup** — Connection tab → Backup & Restore → **Export Config** (saves all sources + mappings to a JSON file). This is your safety net if anything goes wrong.
3. **Get the new version files** — a `publish` folder from the developer (USB drive, network share, SCP, etc.)

## What's in a new version

The new `publish` folder contains updated DLLs and possibly updated scripts:

| File | Overwrite? | Why |
|------|-----------|-----|
| `OpcBridge.App.dll` | ✅ Always | Main app binary — contains all new features and fixes |
| `OpcBridge.Da.dll` | ✅ Always | DA client library — COM, subscriptions, writes |
| `OpcBridge.Core.dll` | ✅ Always | Shared types — TagMapping, BridgeValue, etc. |
| `OpcBridge.Ua.dll` | ✅ Always | UA server library — node manager, write handler |
| `OpcBridge.App.deps.json` | ✅ Always | Dependency manifest — must match the DLLs |
| `OpcBridge.App.runtimeconfig.json` | ✅ Always | Runtime config — may change framework version |
| `*.dll` (Opc.Ua.*, Microsoft.*) | ✅ Always | SDK dependencies — may be updated |
| `appsettings.json` | ⚠️ Only if schema changed | Your DA config — see below |
| `scripts\windows\*.cmd, *.ps1` | ✅ If provided | Launcher and task scripts may change |
| `mappings.json` | ❌ Never | Your tag mappings |
| `sources.json` | ❌ Never | Your saved DA source connections |
| `pki\` | ❌ Never | OPC UA certificates |

## Update steps

### Step 1 — Stop the bridge

```powershell
# Stop the scheduled task
schtasks /end /tn OpcDaToUaBridge

# Wait 2 seconds, then kill any lingering dotnet process
Start-Sleep -Seconds 2
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force

# Verify it's stopped
Get-Process dotnet -ErrorAction SilentlyContinue
# Should return nothing
```

**Why kill dotnet?** The scheduled task may stop but leave the dotnet process running. If you try to overwrite a DLL that's in use, Windows will error with "file in use".

### Step 2 — Copy new files

```powershell
# Copy ALL files from the new publish folder, EXCEPT user data
cd C:\OpcDaToUaBridge

# Copy everything from the new publish folder
xcopy /Y /S <new-publish-folder>\* publish\

# Restore user data if xcopy overwrote them (it shouldn't if you excluded them)
# mappings.json and sources.json should already be excluded — verify:
if (Test-Path publish\mappings.json) { "mappings.json: OK" } else { "mappings.json: MISSING!" }
if (Test-Path publish\sources.json) { "sources.json: OK" } else { "sources.json: MISSING!" }
```

**If `appsettings.json` changed schema** (new config keys added):
- The new `appsettings.json` may have new sections. Merge your existing values into the new file manually.
- Your `mappings.json` and `sources.json` are NOT affected — they override `appsettings.json` at runtime.

### Step 3 — Update scripts (if provided)

```powershell
# Copy updated scripts if the new version includes them
xcopy /Y /S <new-version>\scripts\windows\ scripts\windows\
```

### Step 4 — Restart

```powershell
# Start the bridge
schtasks /run /tn OpcDaToUaBridge

# Wait for startup
Start-Sleep -Seconds 10

# Check health
(Invoke-RestMethod http://localhost:8080/health).status
# Should return: ok
```

### Step 5 — Verify

| Check | How | Expected |
|-------|-----|----------|
| Version | Topbar badge or `/api/version` | New version number |
| Health | `http://localhost:8080/health` | `{"status":"ok"}` |
| Bridge state | Monitor → Bridge | Running |
| DA connection | Monitor → DA | Connected |
| Tag count | Monitor → Tags | Same as before update |
| Mappings | Tags tab | All your tags are still there |
| Sources | Connection tab | All your sources are still there |

If DA shows Faulted after update, check `/api/logs` for errors. If `appsettings.json` was overwritten with wrong DA config, fix the ProgID/Host and restart.

## Quick update (one-liner)

If you have the new publish folder on the same machine:

```powershell
schtasks /end /tn OpcDaToUaBridge; Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2; xcopy /Y /S C:\new-publish\* C:\OpcDaToUaBridge\publish\; schtasks /run /tn OpcDaToUaBridge; Start-Sleep 10; (Invoke-RestMethod http://localhost:8080/health).status
```

## If something goes wrong

1. **Bridge won't start** — check `publish\bridge-task-stderr.log` for the crash error
2. **DA Faulted** — check the dashboard logs tab or `/api/logs?limit=50`; verify `appsettings.json` Da:ProgId and Da:Host
3. **Lost mappings** — restore from the backup JSON: Connection tab → Backup & Restore → Import Config
4. **Roll back** — keep the old `publish` folder renamed to `publish.old`; if the new version fails, stop the task, rename `publish.old` back to `publish`, restart

## What is preserved across updates

| File | Overwritten? | Notes |
|------|-------------|-------|
| `mappings.json` | ❌ Never | Your tag mappings — survives all updates |
| `sources.json` | ❌ Never | Your DA source connections — survives all updates |
| `pki/` | ❌ Never | OPC UA certificates — never overwrite |
| `appsettings.json` | ⚠️ Only if you choose to | Merge new keys into your existing file |
| `bridge-task-*.log` | ✅ Cleared on restart | Old logs are deleted by the launcher |
---

# OPC UA Certificates (PKI)

The bridge uses X.509 certificates to encrypt and authenticate OPC UA communication. This is the **PKI** (Public Key Infrastructure) — a folder of certificates managed automatically.

## Where it lives

```
publish\pki\
  ├── own\          ← the bridge's own certificate (auto-generated on first start)
  ├── trusted\      ← certificates of UA clients you've approved
  ├── issuers\      ← certificate authorities (CAs) that sign client certs
  └── rejected\     ← certificates of UA clients that were rejected
```

## How it works

1. **First start** — the bridge generates a self-signed X.509 certificate and saves it to `pki/own/`. No manual setup needed.
2. **Client connects** — a UA client (e.g. UAExpert) connects to `opc.tcp://host:4840/OpcDaToUaBridge`. The bridge sends its own certificate.
3. **Client trusts the bridge** — the UA client may show a "trust server certificate?" dialog. Accept it.
4. **Bridge trusts the client** — the client's certificate is saved to `pki/trusted/` (if `AutoAcceptUntrustedCertificates` is `true`) or `pki/rejected/` (if `false`).
5. **Encrypted session** — all subsequent UA traffic is encrypted using the negotiated keys.

## AutoAcceptUntrustedCertificates

| Setting | Behavior | Use case |
|---------|----------|----------|
| `true` | Accepts all client certs automatically, saves to `pki/trusted/` | Testing, local network, trusted environment |
| `false` | Rejects unknown client certs → saved to `pki/rejected/` → client cannot connect until you manually move the cert to `pki/trusted/` | Production, untrusted networks |

## Managing certificates

- **Trust a rejected client** — move the `.der` file from `pki/rejected/` to `pki/trusted/` and restart the bridge
- **Regenerate the bridge cert** — delete `pki/own/` and restart; a new certificate is generated. All clients must re-trust the bridge.
- **Never delete `pki/` during updates** — the bridge identity changes if you regenerate, breaking all existing client connections

## Do NOT overwrite `pki/` on update

The `pki/` folder is **user data** — it contains the bridge's identity and trust relationships. Overwriting it during an update:
- Forces the bridge to generate a new certificate
- Breaks all existing UA client connections (they must re-trust)
- Loses the trusted/rejected client list

Always preserve `pki/` across updates. It's listed in the update guide as "never overwrite".

===

# Configuration Reference

## appsettings.json

- **Da:ProgId** — OPC DA server ProgID (e.g. `Matrikon.OPC.Simulation.1`)
- **Da:Host** — DA server host (localhost or remote IP)
- **Da:UpdateRateMs** — default update rate for new sources (min 100ms); can be changed live in Connection tab → Default Update Rate
- **Da:UseSubscriptions** — use `IOPCDataCallback` subscriptions (default `true`); can be toggled live in Connection tab → DA Subscriptions
- **Ua:EndpointUrl** — OPC UA server endpoint (default `opc.tcp://0.0.0.0:4840/OpcDaToUaBridge`)
- **Ua:AutoAcceptUntrustedCertificates** — accept untrusted UA client certs (dev/test)
- **Bridge:RateLimits** — max tags per rate group (rate ms → max tags)
- **Bridge:ExpectedTagCount** — pre-allocation hint for the value cache (default 1000; grows past it)
- **Bridge:Mappings** — initial tag mappings loaded at startup

## Tag mapping fields

| Field | Default | Description |
|-------|---------|-------------|
| `sourceId` | `default` | DA source identifier |
| `daItemId` | *(required)* | OPC DA item ID |
| `uaNodeId` | auto | UA node ID (default `ns=2;s={sourceId}/{daItemId}`) |
| `displayName` | = daItemId | Label shown in UA and dashboard |
| `description` | `null` | Operator-entered notes/description (shown as tooltip in tag list) |
| `dataType` | `Double` | Data type hint for UA node |
| `enabled` | `true` | Include in DA reads and UA publishing |
| `accessRights` | `Read` | `Read` (DA→UA), `Read-Write` (DA↔UA), or `Write` (UA→DA only) |
| `mode` | `Source` | `Source` (live DA value) or `Manual` (simulation — publishes ManualValue) |
| `manualValue` | `null` | Fixed value when mode is `Manual` (simulation) |
| `pollRateMs` | `0` | Per-tag update rate in ms (0 = source default) |
| `deadbandPct` | `0` | Deadband % for subscription filtering (0–100; 0 = no filter) |
| `writeable` | `false` | Derived from accessRights — true for Read-Write and Write |

""";
}
