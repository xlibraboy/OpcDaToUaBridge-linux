# Issues

Real issues — on a plant host, in a field report, or found while developing — written down
by the AI agents that investigate them, newest first, one section per issue. The bridge
embeds this file at build time (see `OpcBridge.App.csproj`) and serves it at
`GET /api/issues`; the dashboard shows it under **Ops ▸ Issues**, visible to **Admin** users
only. Every released bridge therefore carries the issues known at that release, so a host
can answer "is this a known problem?" without opening the repository.

An entry is written when an issue is real, not when a theory is: what broke, where it was
seen, and how it ended — fixed in a version, or still open. Entries are not deleted; an
open issue is updated to **Fixed in <version>** once its fix ships.

---

## Killing a worker left its source "Connected" on stale values (fixed on the isolation branch)

**Status:** Fixed (unreleased, `feature/da-containment-and-workers`) · **Found:** 2026-10-02 ·
**Where:** live branch build; Matrikon simulation source in `own` worker mode · **Area:** OPC DA / Workers

Field report: after **Kill** on Ops ▸ Workers the source kept reporting *Connected* while values
stopped arriving, the coordinator respawned the worker almost at once, and the board's worker row
could disappear entirely so Kill answered 404. Three stacked causes, all in the new isolation code:

- **Kill was undone by the retry loop.** The process died, the next poll failed, and a fresh
  worker existed ~0.4 s later — the source never visibly left *Connected*. Operator Kill is now
  sticky: the worker reports `operator-stopped` and the supervisor refuses new channels until
  **Restart** or a change to the source's worker settings (the settings version releases it).
- **A dead worker was invisible to a push-only source.** With subscriptions active the proxy
  receives pushes and issues no requests, so a closed pipe surfaced only when some later request
  happened to fail. The proxy now watches the transport's `Closed` event
  (`IConnectionLostSource`) and the coordinator reconnects the source immediately.
- **Teardown raced the session rebuild.** Disposing the old proxy and creating the new one in
  the same pass let the "last client released" teardown kill the freshly attached worker — and
  remove the entry while its process was still streaming (row gone, Kill 404, values still
  flowing). Teardown is now a deferred reap that re-checks the client count and the exact
  process instance, and entries carrying operator or crash state are kept.

Verified on the branch build: kill → `STOPPED BY OPERATOR`, values stop, no respawn across 40 s;
**Restart** → new worker pid, values resume. Full suite 1181 green.

---

## Honeywell PMD crashes the bridge during DA subscriptions (open)

**Status:** Open · **Found:** 2026-09-30 · **Where:** PRW11709 (Honeywell
PMD.DDT_OPCDataServer.1) · **Area:** OPC DA

The PMD server is registered as an **in-proc** COM server, so its code runs inside
`OpcBridge.App.exe` and a fault there takes the whole bridge down. With the source
connected, the service died every ~55–60 seconds (SCM event 7031, fifteen restarts in
twenty minutes) about a minute into each OPC DA subscription lifecycle — and left **no**
`crash-*.log`, because the process is killed natively and the managed crash handler never
runs.

Until a post-mortem dump pins the native fault down, run Honeywell PMD sources in **Sync**
mode (`IoMode: "Sync"`, no subscriptions). Next step: enable WER LocalDumps for
`OpcBridge.App.exe` on a lab host and run the PMD source in subscription mode.

The **activation probe** added alongside those fixes is the tool for this fault class, and
its isolation has since been lab-verified on the Windows 11 host (2026-10-01): with an
injected fault-on-activation in-proc COM server the probe's child died alone (exit
`0x80131623`; WER 1000/1025/1001 name the child PID, no `crash-*.log`) while the bridge
stayed up and answered `died: true` — a probe that dies is a trustworthy verdict, and the
bridge's own process never loads the vendor DLL. That does not close this issue: the
subscription-lifecycle crash itself still needs the dump.

Two containment paths now ship for this fault class, neither of which changes the root cause:
a PMD source can be **isolated in a worker process** (Sources → OPC DA → Worker Process;
a fault kills only the worker — the source reconnects, the bridge process is untouched), and a
host that cannot isolate can run the in-proc server **in the system COM surrogate** with
`scripts/windows/enable-pmd-surrogate.ps1` (plus `enable-wer-localdumps.ps1` to finally capture
the dump). See `docs/da-worker-isolation.md` and the field notes.

---

## A DA source subscribed although its Subscription switch was off

**Status:** Fixed in [Unreleased] · **Found:** 2026-09-30 · **Where:** PRW11709 (Honeywell
PMD.DDT_OPCDataServer.1) · **Area:** OPC DA

With I/O mode **AutoDetect** the subscription decision consulted the global master switch
alone, so a source saved with its own Subscription switch off still subscribed — the flag
only took effect after switching the source to **Sync**, the workaround the host needed
before its PMD server stopped receiving callbacks. **AutoDetect** now combines both switches,
the same way the OPC UA path always has.

---

## The DA watchdog reconnected slow plant tags every 60 seconds

**Status:** Fixed in [Unreleased] · **Found:** 2026-09-30 · **Where:** PRW11709 (Honeywell
PMD.DDT_OPCDataServer.1) · **Area:** OPC DA

The watchdog timeout was fixed at 60 s, so a subscription source whose tags legitimately
update less often than a minute was torn down and reconnected on every scan — the flap the
watchdog exists to avoid for everything else. The nested `OpcDa` block of a source now
carries `watchdogTimeoutMs` (`0` disables it), defaulting to the same 60 s when unset.

---

## Every controlled stop wrote a crash report

**Status:** Fixed in [Unreleased] · **Found:** 2026-09-30 · **Where:** PRW11709 (Honeywell
PMD.DDT_OPCDataServer.1) · **Area:** Windows service

When the host's stop window (`HostOptions.ShutdownTimeout`) elapsed while an OPC source was
still being torn down, `WindowsServiceLifetime.StopAsync` threw an
`OperationCanceledException` out of `Main`: every controlled stop wrote a `crash-*.log` and
exited through the unhandled-exception path. The shutdown now notes the timeout on the
durable log and exits quietly.

---

## Event Log logging threw FileNotFoundException in the self-contained build

**Status:** Fixed in [Unreleased] · **Found:** 2026-09-30 · **Where:** PRW11709 (Honeywell
PMD.DDT_OPCDataServer.1) · **Area:** Windows service / packaging

`System.Diagnostics.EventLog` loads `System.Threading.AccessControl` the first time it
writes to a source, and the publish did not carry that assembly — so every Event Log write
failed with `FileNotFoundException` and a harmless event (a client resetting a Kestrel
connection) surfaced as an error and a crash report. The app now references the assembly.

---

## A vendor's browse answered S_FALSE and looked like an empty address space

**Status:** Fixed in [Unreleased] · **Found:** 2026-09-30 · **Where:** In the field — a
vendor DA server whose browse replies end with `S_FALSE` · **Area:** OPC DA / Tag Browser

The Tag Browser treated any non-zero HRESULT from an address-space enumerator as the end of
the list, yet a server may legally return the last names together with `S_FALSE` — one
server answering that way browsed as empty while a better-behaved server on the same
machine listed its tags; failed `OPC_BRANCH` / `OPC_LEAF` calls, a refused `GetItemID`
and an unsupported root move all vanished behind the same empty result. Browse now drains
every item each reply reports, tolerates the root-move and organization differences, and
falls back to a flat (`OPC_FLAT`) browse when a hierarchical walk of the root finds
nothing; what still fails is named above the tag tree.

---

## The bridge could not connect to the PMD server at all

**Status:** Fixed on the host (no app change) · **Found:** 2026-09-30 · **Where:** PRW11709
(Honeywell PMD.DDT_OPCDataServer.1) · **Area:** Deployment

Three stacked host causes: the 32-bit COM registration of the PMD server pointed at a dead
build-machine path (`F:\SetUpWork\...`), the bridge service ran as `LocalSystem` — which
this PMD/DCOM stack does not serve (COM activation hangs; only plant accounts in the local
**Distributed COM Users** group work) — and the DA source pinned `Host = localhost` plus the
account credentials, which produced `800706ba` even as the right account. The registration
was repointed to the installed DLL, the service now logs on as a plant account, and
Host/credentials were cleared; the source then connects and reads tags. Full notes:
`docs/pmd-opc-da-field-notes-2026-09-30.md`.
