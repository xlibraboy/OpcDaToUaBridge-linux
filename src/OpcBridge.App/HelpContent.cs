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

# Troubleshooting

- **DA browse fails** — check ProgID, host reachability, DCOM permissions, and credentials (Connection tab → Credentials section).
- **Values stop moving** — check Monitor → Source Status for connection state and last read timing. Check the alarm bar for rate-group saturation.
- **Tags not appearing in UA** — verify the tag is Enabled and in Source mode (Tags tab → faceplate). Check Monitor → OPC UA Endpoint for node count.
- **Rate group saturated** — the read time exceeds 80% of the poll rate. Increase the rate or reduce the number of tags in that rate group.
- **Tag limit exceeded** — the number of tags in a rate group exceeds the configured limit. Move some tags to a slower rate or increase the limit in `appsettings.json`.

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
