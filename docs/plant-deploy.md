# Plant vs Lab Deploy — OpcBridge

This document defines the two supported Windows deploy modes. The same publish artifact (`publish.tar.gz` `win-x86` framework-dependent) is used; only the scheduled-task logon type and auto-logon differ.

## 1. Lab VM — DESKTOP-BC2AU7H (GX Simulator)

**Why Interactive:** `Mitsubishi GX Simulator` + `MX OPC` use a **per-session shared memory** (`Sim2ComProcEx.dll`). A bridge in `Session 0` (`S4U`) spawns its own `MXOPC 10168@0` isolated from the desktop’s `5236@1` → `Connected` but `CommFault` / `mx1 0x0180800E`. `Matrikon Simulation` is not session-bound and works in `0`, but `Mitsubishi` requires `1`.

**Task:** `Interactive` `Hidden` `Highest`, **two triggers** `AtStartup` + `AtLogOn Tested1`, `Session 1` (`console Tested1 Active`).

```powershell
# one-time on the lab VM:
Set-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon AutoAdminLogon 1
Set-ItemProperty HKLM:\...\Winlogon DefaultUserName Tested1
Set-ItemProperty HKLM:\...\Winlogon DefaultPassword '19891989' # lab only, plaintext
Set-ItemProperty HKLM:\...\Winlogon DefaultDomainName DESKTOP-BC2AU7H

.\scripts\windows\register-published-task.ps1 -LogonType Interactive
# → Hidden True, Triggers AtStartup+AtLogOn, Principal Interactive Highest
# Verify: Get-ScheduledTask OpcBridge | fl; Get-ScheduledTaskInfo; curl http://127.0.0.1:8080/api/status → sessionId 1 interactive true
```

* The dashboard's session-0 banner is **informational only** — the in-app `Resolve` relaunch was removed (#25), because a bridge that is running cannot move itself across sessions. If the bridge comes up as `S4U` (`sessionId 0`, banner appears), re-run `register-published-task.ps1 -LogonType Interactive` and restart the task; after a permanent `Interactive` registration the banner stays `display:none`.

* Closing a **visible** console kills the app (`CTRL_CLOSE_EVENT`). `Interactive Hidden` has no window → close-proof. `S4U` is headless by definition.

## 2. Plant — real PLC hardware (Ethernet, not GX Simulator)

**Why S4U:** Real PLC via `MX OPC` channel `Ethernet` (or `Melsec`, `S7`, `OpcUa` inbound) is **not** session-bound. `S4U @ 0` is correct: starts at boot **without** logon, survives logoff/reboot, no auto-logon needed, no desktop.

```powershell
.\scripts\windows\register-published-task.ps1 -LogonType S4U
# → Hidden False, Trigger AtStartup only, Principal S4U Highest, Session 0
# AutoAdminLogon should be 0/disabled for plant (security).
```

* Plant `sources.json` should **not** contain a GX Simulator-backed source. Use either:
  * `SourceType=OpcDa ProgId=Mitsubishi.MXOPC.6 Host=localhost` with `MX OPC` channel set to **real PLC IP** in `MXConfigurator` (not Simulator), or
  * `SourceType=MelsecA3n` / `S7200Ppi` / `OpcUa` direct drivers (no `MX` at all).

  Note: the `SourceType=MxComponent` source (MELSOFT MX Component COM) was removed in #31. A `sources.json` row that still names it is dropped at bridge start with a `uses the removed 'MxComponent' source type` log line — delete the row and pick one of the two options above.

* Plant `RemoteUsername` only needed for **remote DCOM** (`Host=192.168.x.y` on another PC). For `localhost` leave empty → default credentials (bridge’s `Tested1` token).

## 3. Deploy flow (both)

```bash
# WSL (lab or plant, same artifact):
export PATH="$HOME/.dotnet:$PATH"
dotnet publish src/OpcBridge.App -c Release -r win-x86 --self-contained false -o ./publish.tmp
tar czf publish-new.tar.gz -C publish.tmp .
scp publish-new.tar.gz Tested1@192.168.48.129:C:/.../publish-new.tar.gz
scp scripts/windows/* Tested1@192.168.48.129:C:/.../scripts/windows/
ssh Tested1@192.168.48.129 'powershell -File C:\...\winvm-deploy.ps1' # stops, backup pki/mappings/sources, tar -xzf, restore
ssh Tested1@192.168.48.129 'powershell -File C:\...\register-published-task.ps1 -LogonType Interactive' # lab
# or -LogonType S4U for plant
curl http://192.168.48.129:8080/health # {"status":"ok"}
curl http://192.168.48.129:8080/api/status | jq .bridge.sessionId,.bridge.interactiveSession
# Memory of the deployed bridge (issue #27) — and its Server vs Workstation GC A/B:
scripts/windows/measure-windows-memory.sh --label after-deploy --minutes 10
```

The measurement flow is documented in `docs/ram-measurement.md`.

## 4. Which to use?

| Env | LogonType | Session | Auto-logon | Window | Simulator | Real PLC |
|-----|-----------|---------|------------|--------|-----------|----------|
| Lab `BC2AU7H` | `Interactive` | `1` | **yes** | Hidden | **yes** | yes |
| Plant | `S4U` | `0` | **no** | none | no | **yes** |

The same `775df67+` binary supports both; re-running the script with `-LogonType Interactive` handles an accidental `S4U` registration in lab. For a new plant VM, just run the `S4U` line above and make sure `sources.json` carries no simulation source (an old `MxComponent` row is dropped on start, #31).

The **MSI installs the bridge as a Windows service** (session 0, `LocalSystem`), so an installed bridge cannot serve GX Simulator sources — for the lab, deploy with this script (published task) instead of the installer.
