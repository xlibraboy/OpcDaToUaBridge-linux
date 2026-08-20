# Plant vs Lab Deploy — OpcDaToUaBridge

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
# Verify: Get-ScheduledTask OpcDaToUaBridge | fl; Get-ScheduledTaskInfo; curl http://127.0.0.1:8080/api/status → sessionId 1 interactive true
```

* `Resolve` button (`DashboardPage.cs` + `Program.cs /api/session/resolve` `791742d`+) is a **lab safety net**: if someone redeploys as `S4U` (`sessionId 0` banner appears), one click relaunches via temporary `OpcBridgeResolve` task (`OpcBridge.App.exe` `apphost` preferred, else `dotnet + dll`, `WindowStyle Hidden` `Highest`) into `1`. After permanent `Interactive` the banner stays `display:none`.

* Closing a **visible** console kills the app (`CTRL_CLOSE_EVENT`). `Interactive Hidden` has no window → close-proof. `S4U` is headless by definition.

## 2. Plant — real PLC hardware (Ethernet, not GX Simulator)

**Why S4U:** Real PLC via `MX OPC` channel `Ethernet` (or `Melsec`, `S7`, `OpcUa` inbound) is **not** session-bound. `S4U @ 0` is correct: starts at boot **without** logon, survives logoff/reboot, no auto-logon needed, no desktop.

```powershell
.\scripts\windows\register-published-task.ps1 -LogonType S4U
# → Hidden False, Trigger AtStartup only, Principal S4U Highest, Session 0
# AutoAdminLogon should be 0/disabled for plant (security).
```

* Plant `sources.json` should **not** contain `mx1` `MxComponent Station 0 GX Simulator`. Use either:
  * `SourceType=OpcDa ProgId=Mitsubishi.MXOPC.6 Host=localhost` with `MX OPC` channel set to **real PLC IP** in `MXConfigurator` (not Simulator), or
  * `SourceType=MelsecA3n` / `S7200Ppi` / `OpcUa` direct drivers (no `MX` at all).

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
```

## 4. Which to use?

| Env | LogonType | Session | Auto-logon | Window | Simulator | Real PLC |
|-----|-----------|---------|------------|--------|-----------|----------|
| Lab `BC2AU7H` | `Interactive` | `1` | **yes** | Hidden | **yes** | yes |
| Plant | `S4U` | `0` | **no** | none | no | **yes** |

The same `775df67+` binary supports both; the `Resolve` button handles accidental `S4U` in lab. For a new plant VM, just run the `S4U` line above and delete `mx1` from `sources.json`.
