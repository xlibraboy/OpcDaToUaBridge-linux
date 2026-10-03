# Isolating a crash-prone OPC DA source (worker processes)

An OPC DA source that runs **in process** shares the bridge's address space and its Windows
token. A vendor server registered as an in-proc COM server therefore loads its DLL into the
bridge, and a fault in that DLL kills the whole bridge — the recorded case is Honeywell PMD,
which dies about a minute into every subscription lifecycle (see **Ops ▸ Issues** and
`docs/pmd-opc-da-field-notes-2026-09-30.md`). A **worker process** moves that DLL out of the
bridge: values, writes and metadata still flow through the bridge, but a fault now kills only
the worker, and the source reconnects through the standing retry loop.

## Configuring

**Sources → OPC DA → Worker Process** (the same options travel as `worker` on
`POST /api/da/sources`):

| Mode | What runs where |
|---|---|
| `inProcess` (default) | The client runs inside `OpcBridge.App.exe` — unchanged behaviour. |
| `own` | One dedicated worker process for this source. |
| `group` | One worker process shared by every source with the same run-as account. |

The **Account** fields select the identity the worker logs on as:

- **blank** — the worker inherits the bridge's identity. The zero-config way to isolate a
  crash-prone server.
- **the bridge's own account** (e.g. `.\mesadm1` when the service runs as mesadm1) — same as
  blank, but explicit. No extra rights are needed.
- **another account** — the worker performs a real Windows logon as that account, so the vendor
  DCOM stack sees that SID. Two requirements:
  1. The bridge must run as `LocalSystem` (or an account holding *SeAssignPrimaryToken* and
     *SeIncreaseQuota*), because creating a process as another user needs those privileges.
  2. The target account needs **Log on as a batch job** (`secpol.msc` → Local Policies → User
     Rights Assignment → *Log on as a batch job*).

The run-as password is stored protected with DPAPI (LocalMachine) in `sources.json` and sent
to the worker on **stdin**, never in its command line. A blank password field when editing
means "keep the stored one". `group` without an account is stored as `inProcess`.

## Watching and controlling

**Ops ▸ Workers** (Admin) shows the parent bridge and every worker: mode, pid, account, hosted
sources, rss/private memory, handles, restarts, last exit code and time, last error, heartbeat
age, and the last 50 lifecycle events (`started`, `stopped`, `crashed`, `quarantined`,
`killed`, `restart-requested`). Two actions, both Admin:

- **Restart** — clears quarantine and any operator stop, then stops the worker; the next
  coordinator pass spawns a fresh one.
- **Kill** — stops the worker (confirmation required) and keeps it down: the worker reports
  `operator-stopped`, its sources go to *Reconnecting* with the reason, and nothing respawns
  it until **Restart** or a change to the source's worker settings (mode, account).

`GET /api/workers` is readable by any signed-in user; the actions are Admin-only.

## Failure policy

- A crashed worker is respawned with backoff **1/2/5/10/30 s**. A worker that **fails to
  start** — spawned but never reached its pipe — counts the same way, and its stderr is folded
  into the failure message, so a transient spawn problem recovers on its own. These rules are
  vendor-agnostic: they apply to any OPC DA server isolated in a worker.
- **Five crashes inside ten minutes → quarantined**: no automatic restart, the affected
  sources stay faulted, and the board says why. **Restart** clears it. This is deliberate — a
  crash-looping vendor DLL must not spin forever, and the operator should look at the crash
  evidence first.
- A kill issued from the board is not counted as a crash, and it is sticky (see **Kill** above):
  the retry loop asks for a channel every backoff tick and the supervisor refuses with
  `stopped by the operator` until Restart or a settings change.
- A worker death is reported to its source immediately (the proxy watches the channel's
  `Closed` event), so the source reconnects at once instead of waiting for its next read to
  fail — and a source that only receives pushes can no longer sit on stale values looking
  connected.

## What the worker account sees

The worker runs as its own identity, not the bridge's. It reads the source configuration from
its bootstrap (stdin), talks to its OPC DA server, and reports over the pipe. Its stderr is
forwarded into the bridge log as `[worker {key} pid {pid}]`; a managed crash in the worker also
writes `crash-*.log` when that account can write the data directory, but the forwarded stderr
is the reliable channel. The worker never touches `appsettings.json`, the dashboard or the UA
server. Values, writes, tag metadata, the watchdog, MQTT/Influx and the dashboard behave
exactly as for an in-process source.

## Cost

Each worker is a separate .NET process — roughly the same fixed baseline as the bridge itself
at idle (~30 MB private, nearly independent of tag count), plus the vendor DLL that moved out
of the parent. **Group** mode keeps the process count down: with three sources on one account,
group mode costs one worker instead of three. Only isolate sources that need a different
identity or that are known to fault; everything else can stay in process.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Source faulted, error `Windows logon failed … (Win32 error 1385)` | The run-as account lacks the batch logon right | Grant *Log on as a batch job* to the account |
| `Win32 error 1326` | Wrong account name or password | Re-enter the account/password on the source |
| `Win32 error 1314` when starting a worker under another account | The bridge account cannot create processes as another user | Run the bridge service as `LocalSystem` (or grant the two privileges) |
| `run-as account … needs a password` | Account set without a password | Enter the password, or clear the account |
| Worker missing from the board entirely | The source is not placed: `inProcess`, or `group` without an account (normalized to in-process) | Check the source's Worker mode |
| Source faulted, message `Worker '…' failed to start: … Worker output: …` | The child process was spawned but never reached its pipe; the message carries the child's own stderr | Fix what the worker output names — the child's line is the diagnosis |
| Worker appears, dies every ~1 minute, board shows repeated crashes | The vendor DLL is faulting in the worker (contained) | Capture a dump (see below), run the source in **Sync** mode, or report to the vendor |
| **Kill** left the source on stale values while it still said *Connected* | Worker death was invisible to a push-only proxy (no request in flight) | Fixed — the proxy now watches the channel and the coordinator reconnects immediately |

## Related host-side tools

- `scripts/windows/enable-wer-localdumps.ps1` — WER LocalDumps for `OpcBridge.App.exe` and
  `DllHost.exe` (the worker runs as the same exe). The definitive evidence for a native fault.
- `scripts/windows/enable-pmd-surrogate.ps1` — the alternative for hosts where isolation is
  not an option: run the in-proc server in the system COM surrogate (`dllhost.exe`) as a
  run-as account, with a registry rollback record.
