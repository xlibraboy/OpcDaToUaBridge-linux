# Honeywell PMD OPC DA on PRW11709 — field notes (2026-09-30)

What was wrong when the bridge could not connect to `PMD.DDT_OPCDataServer.1` (Honeywell PMD
Data Access) on PRW11709, what was fixed on the machine, and what the application changes in
this repository cover. Written from the debugging session of 2026-09-30; the deployment-side
copy of this note lives on the host itself
(`C:\ProgramData\OpcBridge\OpcBridge-ISSUES-2026-09-30.txt` and the operator's desktop).

## Why the bridge could not connect

Three stacked causes, all on the host:

1. **The 32-bit COM registration of the PMD server pointed at a dead build-machine path.**
   `HKLM\SOFTWARE\Classes\Wow6432Node\CLSID\{A2152446-BCD9-408C-9C1C-CC5A11FFECC9}\InprocServer32`
   named `F:\SetUpWork\PMD\R800\PMD Data Access\PMD_OPCDataServer\ReleaseUMinDependency\PMD_OPCDataServer.dll`
   — drive `F:` does not exist on the plant machine. Repointed to the installed DLL at
   `C:\Program Files (x86)\Honeywell\PMD\PMD Data Access\PMD_OPCDataServer.dll`.

2. **The bridge service ran as `LocalSystem`, which this PMD/DCOM stack does not serve.**
   COM activation as System or Administrator hangs; only plant accounts in the local
   `Distributed COM Users` group work (on this host: `mesadm1`, `OPCLink`, `opcu1`,
   `QCSAdmin`, `800xAInstaller`) — the same reason Matrikon OPC Explorer worked when run as
   `mesadm1`. The `OpcBridge` service now logs on as `.\mesadm1`.

3. **The DA source pinned `Host = localhost` plus the account credentials.** That produced
   `800706ba` (RPC server unavailable) even as `mesadm1`. Cleared — plain local activation,
   the mode that had been proven to work.

With those three fixed the source connects and reads tags.

## The subscription crash loop

With the source successfully connected, the service still died every ~55-60 seconds
(SCM event 7031 "terminated unexpectedly", recovery restart after 5 s — 15 failures in
20 minutes), leaving no `crash-*.log` behind: the process is killed natively, which is why
the managed crash handler never runs. The correlation is with OPC DA **subscriptions**:
each instance created its group, subscribed (`IOPCDataCallback … Advise cookie 3`) and died
about a minute into the subscription lifecycle. The PMD server is registered as an *in-proc*
COM server, so its code runs inside `OpcBridge.App.exe`; a fault there takes the whole
bridge down.

Running the same source in **sync/polling mode** (`IoMode: "Sync"`, no subscriptions) has
been stable since (6+ minutes without a restart, values updating every second — versus one
death per minute before).

The in-app consequence: the per-source `UseSubscriptions` flag was ignored unless the source
was already in `Sync` mode, and the DA watchdog timeout was fixed at 60 s, so the flap
continued. Both are fixed in this repository (see below).

**Open item for the vendor/app side.** The native crash needs a post-mortem dump to pin
down: enable WER LocalDumps for `OpcBridge.App.exe` on a lab host and run the PMD source in
subscription mode. Until that is understood, run Honeywell PMD sources in `Sync` mode.

## What was changed on the host

| Change | Where | Rollback |
| --- | --- | --- |
| `InprocServer32` → the installed PMD DLL | 32-bit CLSID `{A2152446-…}` | old value: `F:\SetUpWork\…\ReleaseUMinDependency\PMD_OPCDataServer.dll` |
| Service logon `LocalSystem` → `.\mesadm1` | `OpcBridge` service | `sc config OpcBridge obj= LocalSystem` + restart |
| `Host`/credentials cleared, `IoMode: "Sync"` | `C:\ProgramData\OpcBridge\sources.json` | backup `sources.json.bak-cc` |
| `Da:UseSubscriptions: false` | `C:\Program Files (x86)\OpcBridge\appsettings.json` | backup `appsettings.json.bak-cc` |

## What this repository changes

* **Per-source subscriptions are honoured for OPC DA sources.** `AutoDetect` now combines the
  global switch with the source's own `UseSubscriptions` flag (`SourceClientFactory`), matching
  the UA path — so a source saved with subscriptions off no longer subscribes.
* **`watchdogTimeoutMs` per DA source** (nested `OpcDa` block, `0` disables), default
  unchanged at 60 s — slow plant tags no longer force a reconnect every minute.
* **Controlled stops no longer write crash reports.** The `OperationCanceledException` that
  `WindowsServiceLifetime.StopAsync` throws when the host's stop window elapses is caught at
  `Main` and noted on the durable log instead of being reported as an unhandled crash.
* **`System.Threading.AccessControl` ships with the Windows publish** — without it every
  Event Log write threw `FileNotFoundException`, turning harmless Kestrel log lines into
  error spam and crash reports.

## Observed crash reports (for reference)

* `crash-*.log`, `AppDomain.UnhandledException`, `terminating: True`:
  `OperationCanceledException` from `WindowsServiceLifetime.StopAsync` — written on every
  *controlled* stop (this is the defect fixed above; it is not the cause of the 7031 loop).
* `crash-*.log`, `TaskScheduler.UnobservedTaskException`, `terminating: False`:
  `FileNotFoundException: System.Threading.AccessControl` inside `EventLogLogger` — the
  Event Log packaging defect fixed above.
* The 7031 loop itself left **no** crash report — a native fault inside the in-proc PMD
  server during the subscription lifecycle.

## Containment procedures shipped in `scripts/windows/` (added 2026-10-01)

Two host-side tools cover this fault class; neither needs an application change. Both must run
from an **elevated, 64-bit PowerShell** and both print a JSON summary of what they changed.
Lab results from this procedure are to be recorded in this section when they exist.

### Capture the native fault — `enable-wer-localdumps.ps1`

WER LocalDumps writes the dump at the OS level, which is the only way to see the native stack:
a natively-killed process never writes `crash-*.log` (see above).

```powershell
powershell -ExecutionPolicy Bypass -File .\enable-wer-localdumps.ps1 -ProcessName OpcBridge.App.exe,DllHost.exe -GrantUser '.\mesadm1'
```

* Dumps land in `C:\ProgramData\OpcBridge\dumps` (full dumps, five kept per executable); the
  per-executable keys live under
  `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\<exe>`.
* `DllHost.exe` is included because the surrogate procedure below moves the fault there.
* Analyse with WinDbg: `windbg -z <dump>`, then `!analyze -v` (FAULTING module / native stack)
  and `lm` / `k` for the loaded vendor module.
* Remove with `-Remove`; dumps already written are left on disk.

### Contain the in-proc server — `enable-pmd-surrogate.ps1`

The PMD server is registered with `InprocServer32`, so its DLL loads into the bridge and a
fault takes the bridge down. Setting `DllSurrogate` on the server's AppID makes COM activate
it inside `dllhost.exe` instead — the fault then kills only the surrogate.

```powershell
powershell -ExecutionPolicy Bypass -File .\enable-pmd-surrogate.ps1 -RunAsPassword '<mesadm1 password>'
```

* Changes, in the 32-bit registry view by default (the x86 bridge reads `Wow6432Node`):
  `AppID` on the CLSID (created if absent), `DllSurrogate = ""` (the system surrogate) and
  `RunAs = ".\mesadm1,<password>"` on the AppID key.
* A rollback record (`reg export` + the previous values as JSON) is written under
  `C:\ProgramData\OpcBridge\rollback\` before anything changes; `-Rollback` restores the
  newest record.
* Preconditions checked and reported: the account needs **Log on as a batch job**
  (`SeBatchLogonRight`); a `LocalServer32` on the CLSID refuses the change (the server already
  runs out of process). Without `-RunAsPassword` the script changes nothing and prints the
  manual `reg add` lines.
* Verification after the change: the PMD source connects;
  `(Get-Process dllhost).Modules | Where-Object ModuleName -match 'PMD'` lists the vendor DLL;
  running the source with subscriptions for **more than five minutes** with the bridge PID and
  `/health` unchanged proves containment. The running bridge keeps its old session until the
  source reconnects (or the bridge restarts).
* Risk: not every in-proc server tolerates surrogate isolation, and `IOPCDataCallback`
  callbacks now cross a marshalling boundary. If PMD misbehaves under it, roll back and use the
  worker-process design instead.

| Change | Where | Rollback |
| --- | --- | --- |
| WER LocalDumps for `OpcBridge.App.exe` / `DllHost.exe` | `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps` | `enable-wer-localdumps.ps1 -Remove` |
| PMD activation via `dllhost.exe` as `.\mesadm1` | 32-bit `AppID` of CLSID `{A2152446-…}` | `enable-pmd-surrogate.ps1 -Rollback` |

**Lab result (to fill in):** containment proven / not tolerated / still crashing — evidence,
dump name, and the final `ISSUES.md` status.
