# DA Links

DA Links are a **separate subsystem** from DA → UA mappings. A provider change on one OPC DA source can write directly to a consumer on another OPC DA source through the bridge's shared DA runtime without changing the mapping payload for that consumer.

## How it works

- The **provider** tag is read from its DA source normally and must have Access Rights that include **Read**.
- The **consumer** tag keeps its own mapping and must have Access Rights that include **Write** or **Read-Write** so the bridge can forward provider changes into its DA server.
- DA Links share the bridge runtime with mappings, so cross-source forwarding works even when the provider and consumer live on different OPC DA servers.
- Runtime forwarding is driven by stored `DaLinkRule` entries. Legacy `providerSourceId` / `providerItemId` fields exist only for migration from older mapping files.

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
