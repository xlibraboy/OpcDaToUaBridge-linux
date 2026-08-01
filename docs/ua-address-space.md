# Unified UA Address Space

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
