param(
    [string]$TaskName = 'OpcDaToUaBridge',
    [string]$HealthUrl = 'http://127.0.0.1:8080/health',
    [int]$ProbeSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptRoot)
$publishDir = Join-Path $repoRoot 'publish'
$publishExe = Join-Path $publishDir 'OpcBridge.App.exe'
$cmdScript = Join-Path $scriptRoot 'start-published-bridge.cmd'

if (-not (Test-Path $publishExe)) {
    throw "Publish exe not found: $publishExe"
}

if (-not (Test-Path $cmdScript)) {
    throw "Launcher cmd not found: $cmdScript"
}

Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'OpcBridge.App.exe' -or ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*OpcBridge.App*')
} | ForEach-Object {
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
}

$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$cmdScript`""
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\$env:USERNAME" -LogonType S4U -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 0)

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
    $listener = Get-NetTCPConnection -LocalPort 8080 -State Listen | Select-Object LocalAddress, LocalPort, OwningProcess
} catch {
}

[pscustomobject]@{
    repoRoot = $repoRoot
    publishExeExists = Test-Path $publishExe
    cmdScriptExists = Test-Path $cmdScript
    health = if ($health) { $health.status } else { 'down' }
    taskState = (Get-ScheduledTask -TaskName $TaskName).State.ToString()
    lastTaskResult = (Get-ScheduledTaskInfo -TaskName $TaskName).LastTaskResult
    listener = $listener
} | ConvertTo-Json -Depth 4 -Compress
