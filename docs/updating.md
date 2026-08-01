# Updating to a New Version

Updates are **local only** — no internet, no admin. Overwrite the DLLs and restart. Your config and tag mappings are preserved.

## Before you update

1. **Check current version** — look at the topbar badge (e.g. **v1.0.0**) or call `http://localhost:8080/api/version`
2. **Export a backup** — Sources → OPC DA → Backup & Restore → **Export Config** (saves all sources + mappings to a JSON file). This is your safety net if anything goes wrong.
3. **Get the new version files** — a `publish` folder from the developer (USB drive, network share, SCP, etc.)

## What's in a new version

The new `publish` folder contains updated DLLs and possibly updated scripts:

| File | Overwrite? | Why |
|------|-----------|-----|
| `OpcBridge.App.dll` | ✅ Always | Main app binary — contains all new features and fixes |
| `OpcBridge.Da.dll` | ✅ Always | DA client library — COM, subscriptions, writes |
| `OpcBridge.Core.dll` | ✅ Always | Shared types — TagMapping, BridgeValue, etc. |
| `OpcBridge.Ua.dll` | ✅ Always | UA server library — node manager, write handler |
| `OpcBridge.App.deps.json` | ✅ Always | Dependency manifest — must match the DLLs |
| `OpcBridge.App.runtimeconfig.json` | ✅ Always | Runtime config — may change framework version |
| `*.dll` (Opc.Ua.*, Microsoft.*) | ✅ Always | SDK dependencies — may be updated |
| `appsettings.json` | ⚠️ Only if schema changed | Your DA config — see below |
| `scripts\windows\*.cmd, *.ps1` | ✅ If provided | Launcher and task scripts may change |
| `mappings.json` | ❌ Never | Your tag mappings |
| `sources.json` | ❌ Never | Your saved DA source connections |
| `pki\` | ❌ Never | OPC UA certificates |

## Update steps

### Step 1 — Stop the bridge

```powershell
# Stop the scheduled task
schtasks /end /tn OpcDaToUaBridge

# Wait 2 seconds, then kill any lingering dotnet process
Start-Sleep -Seconds 2
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force

# Verify it's stopped
Get-Process dotnet -ErrorAction SilentlyContinue
# Should return nothing
```

**Why kill dotnet?** The scheduled task may stop but leave the dotnet process running. If you try to overwrite a DLL that's in use, Windows will error with "file in use".

### Step 2 — Copy new files

```powershell
# Copy ALL files from the new publish folder, EXCEPT user data
cd C:\OpcDaToUaBridge

# Copy everything from the new publish folder
xcopy /Y /S <new-publish-folder>\* publish\

# Restore user data if xcopy overwrote them (it shouldn't if you excluded them)
# mappings.json and sources.json should already be excluded — verify:
if (Test-Path publish\mappings.json) { "mappings.json: OK" } else { "mappings.json: MISSING!" }
if (Test-Path publish\sources.json) { "sources.json: OK" } else { "sources.json: MISSING!" }
```

**If `appsettings.json` changed schema** (new config keys added):
- The new `appsettings.json` may have new sections. Merge your existing values into the new file manually.
- Your `mappings.json` and `sources.json` are NOT affected — they override `appsettings.json` at runtime.

### Step 3 — Update scripts (if provided)

```powershell
# Copy updated scripts if the new version includes them
xcopy /Y /S <new-version>\scripts\windows\ scripts\windows\
```

### Step 4 — Restart

```powershell
# Start the bridge
schtasks /run /tn OpcDaToUaBridge

# Wait for startup
Start-Sleep -Seconds 10

# Check health
(Invoke-RestMethod http://localhost:8080/health).status
# Should return: ok
```

### Step 5 — Verify

| Check | How | Expected |
|-------|-----|----------|
| Version | Topbar badge or `/api/version` | New version number |
| Health | `http://localhost:8080/health` | `{"status":"ok"}` |
| Bridge state | Monitor → Bridge | Running |
| DA connection | Monitor → DA | Connected |
| Tag count | Monitor → Tags | Same as before update |
| Mappings | Tags tab | All your tags are still there |
| Sources | Sources → OPC DA | All your sources are still there |

If DA shows Faulted after update, check `/api/logs` for errors. If `appsettings.json` was overwritten with wrong DA config, fix the ProgID/Host and restart.

## Quick update (one-liner)

If you have the new publish folder on the same machine:

```powershell
schtasks /end /tn OpcDaToUaBridge; Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2; xcopy /Y /S C:\new-publish\* C:\OpcDaToUaBridge\publish\; schtasks /run /tn OpcDaToUaBridge; Start-Sleep 10; (Invoke-RestMethod http://localhost:8080/health).status
```

## If something goes wrong

1. **Bridge won't start** — check `publish\bridge-task-stderr.log` for the crash error
2. **DA Faulted** — check the dashboard logs tab or `/api/logs?limit=50`; verify `appsettings.json` Da:ProgId and Da:Host
3. **Lost mappings** — restore from the backup JSON: Sources → OPC DA → Backup & Restore → Import Config
4. **Roll back** — keep the old `publish` folder renamed to `publish.old`; if the new version fails, stop the task, rename `publish.old` back to `publish`, restart

## What is preserved across updates

| File | Overwritten? | Notes |
|------|-------------|-------|
| `mappings.json` | ❌ Never | Your tag mappings — survives all updates |
| `sources.json` | ❌ Never | Your DA source connections — survives all updates |
| `pki/` | ❌ Never | OPC UA certificates — never overwrite |
| `appsettings.json` | ⚠️ Only if you choose to | Merge new keys into your existing file |
| `bridge-task-*.log` | ✅ Cleared on restart | Old logs are deleted by the launcher |
