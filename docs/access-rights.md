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
