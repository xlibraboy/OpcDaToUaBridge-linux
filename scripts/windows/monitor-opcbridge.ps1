param(
    [int]$Samples = 12,
    [int]$IntervalSec = 10,
    [string]$OutFile = '',
    [string]$BridgeUrl = 'http://127.0.0.1:8080',
    [string]$BridgeUser = '',
    [string]$BridgePassword = '',
    [int]$LogTailLines = 500
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Samples the running OpcBridge process and reports whether memory, handles and threads are
# climbing. The pattern tells the leak apart:
#   threads + handles rising        -> a DA source keeps failing to connect; each retry leaks its
#                                      STA thread, client and COM thread handle (BridgeWorker
#                                      connect retry never disposes the half-built client)
#   handles rising at poll cadence  -> a DA server rejects Advise; the fallback-to-polling path
#                                      leaks one connection point per poll tick
#   private bytes rising, handles/  -> managed growth (inbound MQTT topics, interlink stats,
#   threads flat                       rate buckets) rather than COM
#
# Usage (no arguments needed):
#   powershell -ExecutionPolicy Bypass -File .\monitor-opcbridge.ps1
#   powershell -ExecutionPolicy Bypass -File .\monitor-opcbridge.ps1 -Samples 30 -IntervalSec 10
#   powershell -ExecutionPolicy Bypass -File .\monitor-opcbridge.ps1 -BridgeUser <user> -BridgePassword <pass>
#
# The last form signs in to the dashboard API and adds the app's own counters (handle count,
# GDI/USER, per-source STA thread health, write-queue depth, UA bandwidth, tag problems).

Add-Type -Namespace Win32 -Name GuiResources -MemberDefinition @'
[DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
'@ -ErrorAction SilentlyContinue

function Resolve-BridgeTarget {
    $service = Get-CimInstance Win32_Service -Filter "Name='OpcBridge'" -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.ProcessId -gt 0) {
        return [pscustomobject]@{
            Pid         = [int]$service.ProcessId
            Kind        = 'service'
            ServiceName = $service.Name
            ServiceState = $service.State
            StartMode   = $service.StartMode
        }
    }

    $process = Get-Process -Name 'OpcBridge.App' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $process) {
        return [pscustomobject]@{
            Pid         = $process.Id
            Kind        = 'process'
            ServiceName = ''
            ServiceState = ''
            StartMode   = ''
        }
    }

    $dllHost = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*OpcBridge.App.dll*' } | Select-Object -First 1
    if ($null -ne $dllHost) {
        return [pscustomobject]@{
            Pid         = [int]$dllHost.ProcessId
            Kind        = 'dotnet'
            ServiceName = ''
            ServiceState = ''
            StartMode   = ''
        }
    }

    throw "OpcBridge process not found. Neither the 'OpcBridge' service, OpcBridge.App.exe nor a dotnet host running OpcBridge.App.dll is running on $env:COMPUTERNAME."
}

function Get-Sample {
    param([int]$ProcessId)

    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    $gdi = 0
    $user = 0
    try {
        $gdi = [int][Win32.GuiResources]::GetGuiResources($process.Handle, 0)
        $user = [int][Win32.GuiResources]::GetGuiResources($process.Handle, 1)
    }
    catch {
        # Handle may be unavailable for a service in another session; the rest still applies.
    }

    return [pscustomobject]@{
        Time       = Get-Date
        WorkingSet = [math]::Round($process.WorkingSet64 / 1MB, 1)
        Private    = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
        Paged      = [math]::Round($process.PagedMemorySize64 / 1MB, 1)
        Virtual    = [math]::Round($process.VirtualMemorySize64 / 1MB, 1)
        Handles    = $process.HandleCount
        Threads    = $process.Threads.Count
        Gdi        = $gdi
        User       = $user
        CpuSeconds = [math]::Round($process.TotalProcessorTime.TotalSeconds, 1)
    }
}

function Show-Rate {
    param(
        [string]$Name,
        [double]$Total,
        [double]$Minutes,
        [string]$Unit
    )
    if ($Minutes -le 0) { return "${Name}: no elapsed time" }
    $rate = $Total / $Minutes
    return ("{0}: {1}{2} over window, {3}{2}/min" -f $Name, [math]::Round($Total, 1), $Unit, [math]::Round($rate, 2))
}

Write-Host "==> Locating the running bridge"
$target = Resolve-BridgeTarget
$process = Get-Process -Id $target.Pid
$path = ''
$startTime = ''
try { $path = $process.Path } catch { }
try { $startTime = $process.StartTime.ToString('yyyy-MM-dd HH:mm:ss') } catch { }
$uptimeHours = 0.0
try { $uptimeHours = [math]::Round(((Get-Date) - $process.StartTime).TotalHours, 2) } catch { }

Write-Host "    computer     : $env:COMPUTERNAME"
Write-Host "    process      : $($process.ProcessName) (pid $($target.Pid), $($target.Kind))"
Write-Host "    path         : $path"
Write-Host "    started      : $startTime (uptime $uptimeHours h)"
Write-Host "    session id   : $($process.SessionId)"
if ($target.Kind -eq 'service') {
    Write-Host "    service      : $($target.ServiceName) state=$($target.ServiceState) startMode=$($target.StartMode)"
}
$task = Get-ScheduledTask -TaskName 'OpcBridge' -ErrorAction SilentlyContinue
if ($null -ne $task) {
    Write-Host "    scheduled    : $($task.State) logon=$($task.Principal.LogonType)"
}

Write-Host ""
Write-Host "==> Sampling $Samples time(s), every $IntervalSec s"
Write-Host "    (working set / private / handles / threads / gdi+user)"

$history = @()
for ($i = 1; $i -le $Samples; $i++) {
    $sample = Get-Sample -ProcessId $target.Pid
    $history += $sample
    Write-Host ("    [{0,2}/{1}] {2:HH:mm:ss}  WS {3,8} MB  private {4,8} MB  handles {5,6}  threads {6,4}  gdi+user {7,5}  cpu {8,7} s" -f `
        $i, $Samples, $sample.Time, $sample.WorkingSet, $sample.Private, $sample.Handles, $sample.Threads, ($sample.Gdi + $sample.User), $sample.CpuSeconds)
    if ($i -lt $Samples) { Start-Sleep -Seconds $IntervalSec }
}

$first = $history[0]
$last = $history[$history.Count - 1]
$minutes = ($last.Time - $first.Time).TotalMinutes
if ($minutes -le 0) { $minutes = $IntervalSec / 60.0 }

$deltaWorkingSet = $last.WorkingSet - $first.WorkingSet
$deltaPrivate = $last.Private - $first.Private
$deltaHandles = $last.Handles - $first.Handles
$deltaThreads = $last.Threads - $first.Threads
$deltaGdiUser = ($last.Gdi + $last.User) - ($first.Gdi + $first.User)

Write-Host ""
Write-Host "==> Trend ($([math]::Round($minutes, 2)) min window)"
Write-Host "    $(Show-Rate -Name 'working set' -Total $deltaWorkingSet -Minutes $minutes -Unit ' MB')"
Write-Host "    $(Show-Rate -Name 'private' -Total $deltaPrivate -Minutes $minutes -Unit ' MB')"
Write-Host "    $(Show-Rate -Name 'handles' -Total $deltaHandles -Minutes $minutes -Unit '')"
Write-Host "    $(Show-Rate -Name 'gdi+user' -Total $deltaGdiUser -Minutes $minutes -Unit '')"
Write-Host "    threads: $deltaThreads change"

$handleRate = $deltaHandles / $minutes
$privateRate = $deltaPrivate / $minutes
$verdicts = @()

if ($deltaThreads -ge 2) {
    $verdicts += "THREAD LEAK SUSPECT: +$deltaThreads threads - each failed DA connect leaves one STA thread behind (BridgeWorker connect retry / OpcDaClient.OpcComThread)."
}
if ($handleRate -ge 40) {
    $verdicts += "HANDLE LEAK SUSPECT: +$([math]::Round($handleRate, 1)) handles/min. If a DA source is failing to connect, suspect the abandoned client; if a source is connected but only polling, suspect the rejected-Advise path leaking one connection point per poll."
}
elseif ($handleRate -ge 10) {
    $verdicts += "Handles are drifting (+$([math]::Round($handleRate, 1))/min) - watch, and check whether a DA source sits in 'Reconnecting'."
}
if ($privateRate -ge 10) {
    $verdicts += "MEMORY GROWTH SUSPECT: +$([math]::Round($privateRate, 1)) MB/min private bytes. If handles and threads are flat this is managed growth (inbound MQTT topics, interlink stats, rate buckets)."
}
elseif ($privateRate -ge 2) {
    $verdicts += "Private bytes climbing slowly (+$([math]::Round($privateRate, 1)) MB/min) - keep sampling longer to confirm."
}
if ($verdicts.Count -eq 0) {
    $verdicts += "No growth trend in this window: working set sawtooth with flat private bytes, handles and threads is normal managed-GC behaviour."
}

Write-Host ""
Write-Host "==> Verdict"
foreach ($verdict in $verdicts) { Write-Host "    $verdict" }

if ([string]::IsNullOrWhiteSpace($OutFile)) {
    $OutFile = Join-Path $env:TEMP ("opcbridge-memory-{0:yyyyMMdd-HHmmss}.csv" -f (Get-Date))
}
$history | Export-Csv -Path $OutFile -NoTypeInformation -Encoding UTF8
Write-Host ""
Write-Host "    csv: $OutFile"

# ---- App's own counters (needs a dashboard sign-in) ----
Write-Host ""
if ($BridgeUser) {
    Write-Host "==> App counters from $BridgeUrl"
    try {
        $webSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $body = @{ username = $BridgeUser; password = $BridgePassword } | ConvertTo-Json
        Invoke-RestMethod -Uri "$BridgeUrl/api/auth/login" -Method Post -ContentType 'application/json' -Body $body -WebSession $webSession | Out-Null

        $status = Invoke-RestMethod -Uri "$BridgeUrl/api/status" -WebSession $webSession
        $diagnostics = Invoke-RestMethod -Uri "$BridgeUrl/api/diagnostics" -WebSession $webSession

        if ($null -ne $status.bridge.resources) {
            Write-Host "    app resources   : handles $($status.bridge.resources.handleCount), gdi $($status.bridge.resources.gdiObjects), user $($status.bridge.resources.userObjects), supported $($status.bridge.resources.supported)"
        }
        Write-Host "    app uptime      : $($diagnostics.uptimeSeconds) s"
        Write-Host "    bridge state    : $($diagnostics.runtime.bridgeState) / DA $($diagnostics.runtime.daConnectionState), mappings $($diagnostics.runtime.mappingCount)"
        if ($null -ne $diagnostics.bridge.staThreads) {
            foreach ($sta in $diagnostics.bridge.staThreads) {
                Write-Host "    sta thread      : source $($sta.sourceId) alive=$($sta.alive) queued=$($sta.queuedItems) last=$($sta.lastActionUtc)"
            }
        }
        if ($null -ne $diagnostics.bridge.writeQueue) {
            Write-Host "    write queue     : depth $($diagnostics.bridge.writeQueue.currentDepth), enqueued $($diagnostics.bridge.writeQueue.totalEnqueued), failed $($diagnostics.bridge.writeQueue.totalFailed)"
        }
        if ($null -ne $diagnostics.bridge.uaBandwidth) {
            Write-Host "    ua bandwidth    : $([math]::Round($diagnostics.bridge.uaBandwidth.notificationsPerSec, 1)) notif/s, ~$([math]::Round($diagnostics.bridge.uaBandwidth.estimatedBytesPerSec, 0)) B/s"
        }
        if ($null -ne $diagnostics.ua.sessions) {
            Write-Host "    ua sessions     : $($diagnostics.ua.sessions.Count) with $($diagnostics.ua.subscriptions.Count) subscription(s)"
        }
        if ($null -ne $diagnostics.problems) {
            Write-Host "    problems        : disconnected $($diagnostics.problems.disconnected.Count), bad quality $($diagnostics.problems.badQualityTotal)"
        }
    }
    catch {
        Write-Host "    app counters unavailable: $($_.Exception.Message)"
    }
}
else {
    Write-Host "==> App counters skipped (pass -BridgeUser/-BridgePassword to include /api/status and /api/diagnostics)"
}

# ---- Connection-failure evidence in the publish logs ----
Write-Host ""
if (-not [string]::IsNullOrWhiteSpace($path)) {
    $publishDir = Split-Path -Parent $path
    $logFiles = @(@(
        (Join-Path $publishDir 'bridge-task-stdout.log'),
        (Join-Path $publishDir 'bridge-task-stderr.log')
    ) | Where-Object { Test-Path $_ })
    if ($logFiles.Count -gt 0) {
        Write-Host "==> Connection-failure evidence in publish logs"
        foreach ($logFile in $logFiles) {
            $lines = Get-Content $logFile -Tail $LogTailLines
            $retry = @($lines | Select-String -SimpleMatch 'connection lost; will retry')
            $advise = @($lines | Select-String -SimpleMatch 'falling back to polling')
            Write-Host "    $(Split-Path -Leaf $logFile): $($retry.Count) 'connection lost; will retry', $($advise.Count) 'falling back to polling' (last $LogTailLines lines)"
            if ($retry.Count -gt 0) {
                Write-Host "      last retry : $($retry[$retry.Count - 1].Line.Trim())"
            }
            if ($advise.Count -gt 0) {
                Write-Host "      last advise: $($advise[$advise.Count - 1].Line.Trim())"
            }
        }
        Write-Host "    A repeating 'connection lost; will retry' for a down source is what leaks one STA thread per attempt."
    }
    else {
        Write-Host "==> No publish logs beside $publishDir (service install logs to the Windows event log instead)"
    }
}

Write-Host ""
Write-Host "==> Done. Send back the verdict lines and the csv above."
