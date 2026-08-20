param(
    [string]$TaskName = 'OpcDaToUaBridge',
    [string]$HealthUrl = '',
    [int]$ProbeSeconds = 20,
    # S4U runs the bridge in session 0 (no interactive desktop). Use 'Interactive'
    # when a source needs an interactive session — e.g. MELSOFT MX Component
    # talking to GX Simulator, whose shared memory is session-bound.
    [ValidateSet('S4U', 'Interactive')]
    [string]$LogonType = 'S4U'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptRoot)
$publishDir = Join-Path $repoRoot 'publish'
 $publishDll = Join-Path $publishDir 'OpcBridge.App.dll'

# Resolve the runtime HTTP port from appsettings.json (the bridge auto-assigns
# a non-default port when 8080 is already in use on the host).
$appSettings = Join-Path $publishDir 'appsettings.json'
$httpPort = 8080
try {
    if (Test-Path $appSettings) {
        $cfg = Get-Content $appSettings -Raw | ConvertFrom-Json
        if ($cfg.Bridge.HttpPort -and $cfg.Bridge.HttpPort -gt 0) {
            $httpPort = [int]$cfg.Bridge.HttpPort
        }
    }
} catch {
    # fall back to 8080
}
if (-not $HealthUrl) {
    $HealthUrl = "http://127.0.0.1:$httpPort/health"
}
 $cmdScript = Join-Path $scriptRoot 'start-published-bridge.cmd'
 
 if (-not (Test-Path $publishDll)) {
     throw "Publish dll not found: $publishDll"
 }

 Get-CimInstance Win32_Process | Where-Object {
     $_.Name -eq 'OpcBridge.App.exe' -or ($_.Name -eq 'dotnet.exe' -and ($_.CommandLine -like '*OpcBridge.App.dll*' -or $_.CommandLine -like '*OpcBridge.App*'))
 } | ForEach-Object {
     Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
 }

# Prefer direct apphost exe (no visible cmd wrapper) — Hidden task hides console, closing window no longer kills bridge
 $publishExe = Join-Path $publishDir 'OpcBridge.App.exe'
 if (Test-Path $publishExe) {
     $action = New-ScheduledTaskAction -Execute $publishExe -WorkingDirectory $publishDir
     $cmdScriptExists = $true
 } else {
     if (-not (Test-Path $cmdScript)) { throw "Launcher cmd not found: $cmdScript and apphost exe not found: $publishExe" }
     $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$cmdScript`""
     $cmdScriptExists = Test-Path $cmdScript
 }
if ($LogonType -eq 'Interactive') {
    $trigger = @(
        (New-ScheduledTaskTrigger -AtStartup)
        (New-ScheduledTaskTrigger -AtLogOn -User "$env:COMPUTERNAME\$env:USERNAME")
    )
    $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\$env:USERNAME" -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 0) -Hidden
} else {
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\$env:USERNAME" -LogonType S4U -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 0)
}

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName

$health = $null
for ($i = 0; $i -lt $ProbeSeconds; $i++) {
    Start-Sleep -Seconds 1
    try {
        $health = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 3
        if ($health.status -eq 'ok') { break }
    } catch {
    }
}

$listener = @()
try {
    $listener = Get-NetTCPConnection -LocalPort $httpPort -State Listen | Select-Object LocalAddress, LocalPort, OwningProcess
} catch {
}

[pscustomobject]@{
    repoRoot = $repoRoot
     publishDllExists = Test-Path $publishDll
    cmdScriptExists = Test-Path $cmdScript
    health = if ($health) { $health.status } else { 'down' }
    taskState = (Get-ScheduledTask -TaskName $TaskName).State.ToString()
    lastTaskResult = (Get-ScheduledTaskInfo -TaskName $TaskName).LastTaskResult
    listener = $listener
} | ConvertTo-Json -Depth 4 -Compress
