# OPC UA Server

- The bridge runs a built-in OPC UA server. UA clients connect to the endpoint shown on the Monitor tab.
- Each DA tag mapping creates one UA variable node under the "OPC DA Tags" folder (namespace index 2).
- Node IDs follow the pattern `ns=2;s={sourceId}/{itemId}` unless a custom UA Node ID is specified.
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
