param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Server', 'Workstation')]
    [string]$Mode,

    [string]$PublishDir = '',
    [string]$HealthUrl = '',
    [int]$ProbeSeconds = 30,
    [switch]$NoRestart
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Switches the deployed bridge between Server and Workstation GC — the A/B for #27
# ("the RAM of the OpcBridge app service on Windows is high").
#
# No rebuild is involved: the .NET host reads System.GC.Server from
# OpcBridge.App.runtimeconfig.json at startup, which is exactly the property a
# <ServerGarbageCollection> change in OpcBridge.App.csproj writes. Measuring this file
# therefore measures what the shipped change would do. The runtimeconfig is read once, at
# process start, so the bridge is restarted to apply the switch.
#
# Server is the shipped default — Microsoft.NET.Sdk.Web sets System.GC.Server true — and
# gives the service one GC heap per core. Workstation keeps a single heap and a smaller
# working set, which is what a bridge (not a throughput web app) wants.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\set-bridge-gc-mode.ps1 -Mode Workstation
#   powershell -ExecutionPolicy Bypass -File .\set-bridge-gc-mode.ps1 -Mode Server
#
# From Linux/WSL, use scripts/windows/measure-windows-memory.sh --gc-mode <mode>: it
# switches, restarts, warms up, samples with monitor-opcbridge.ps1 and pulls the CSV back.
# See docs/ram-measurement.md.

function Resolve-BridgeProcess {
    $service = Get-CimInstance Win32_Service -Filter "Name='OpcBridge'" -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.ProcessId -gt 0) {
        return [pscustomobject]@{ Pid = [int]$service.ProcessId; Kind = 'service' }
    }

    $process = Get-Process -Name 'OpcBridge.App' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $process) {
        return [pscustomobject]@{ Pid = $process.Id; Kind = 'process' }
    }

    $dllHost = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*OpcBridge.App.dll*' } | Select-Object -First 1
    if ($null -ne $dllHost) {
        return [pscustomobject]@{ Pid = [int]$dllHost.ProcessId; Kind = 'dotnet' }
    }

    return $null
}

$desired = 'false'
if ($Mode -eq 'Server') {
    $desired = 'true'
}

Write-Host "==> Locating the deployed bridge"
$target = Resolve-BridgeProcess
if ($null -ne $target) {
    $process = Get-Process -Id $target.Pid -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        Write-Host "    process : $($process.ProcessName) (pid $($target.Pid), $($target.Kind))"
        if ([string]::IsNullOrWhiteSpace($PublishDir)) {
            try {
                $processPath = $process.Path
                if (-not [string]::IsNullOrWhiteSpace($processPath)) {
                    $PublishDir = Split-Path -Parent $processPath
                }
            }
            catch {
                # Elevated / other-session process: the path is unavailable, fall through to the default.
            }
        }
    }
}
else {
    Write-Host "    no running bridge found - the switch still applies to the next start"
}

if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $env:USERPROFILE 'Documents\OpcBridge\publish'
}

$runtimeConfig = Join-Path $PublishDir 'OpcBridge.App.runtimeconfig.json'
if (-not (Test-Path -LiteralPath $runtimeConfig)) {
    throw "OpcBridge.App.runtimeconfig.json not found in '$PublishDir'. Pass -PublishDir pointing at the deployed publish folder (an MSI install keeps it in 'C:\Program Files\OpcBridge')."
}

$text = Get-Content -LiteralPath $runtimeConfig -Raw
$pattern = '"System\.GC\.Server"\s*:\s*(true|false)'
$before = 'absent'
if ($text -match $pattern) {
    $before = $Matches[1]
    $regex = [regex]::new($pattern)
    $updated = $regex.Replace($text, "`"System.GC.Server`": $desired", 1)
}
else {
    $markerIndex = $text.IndexOf('"configProperties"', [StringComparison]::Ordinal)
    if ($markerIndex -lt 0) {
        throw "No 'configProperties' block in $runtimeConfig - not a .NET runtimeconfig file?"
    }

    $braceIndex = $text.IndexOf('{', $markerIndex)
    if ($braceIndex -lt 0) {
        throw "Malformed 'configProperties' block in $runtimeConfig."
    }

    $updated = $text.Insert($braceIndex + 1, "`r`n      `"System.GC.Server`": $desired,")
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($runtimeConfig, $updated, $utf8)

Write-Host "==> GC mode -> $Mode"
Write-Host "    runtimeconfig    : $runtimeConfig"
Write-Host "    System.GC.Server : $before -> $desired"

$after = 'absent'
$verify = Get-Content -LiteralPath $runtimeConfig -Raw
if ($verify -match $pattern) {
    $after = $Matches[1]
}
if ($after -ne $desired) {
    throw "Wrote $runtimeConfig but read back System.GC.Server=$after - the file was not updated."
}

$restarted = $false
$health = $null
if ($NoRestart) {
    Write-Host "==> Skipping the restart (-NoRestart): the switch applies on the next bridge start"
}
else {
    Write-Host "==> Restarting the bridge to apply it"
    $service = Get-CimInstance Win32_Service -Filter "Name='OpcBridge'" -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        if ($service.State -eq 'Stopped') {
            Start-Service -Name 'OpcBridge'
        }
        else {
            Restart-Service -Name 'OpcBridge' -Force
        }
        $restarted = $true
        Write-Host "    service 'OpcBridge' restarted (state $((Get-Service -Name 'OpcBridge').Status))"
    }
    else {
        $task = Get-ScheduledTask -TaskName 'OpcBridge' -ErrorAction SilentlyContinue
        if ($null -ne $task) {
            Stop-ScheduledTask -TaskName 'OpcBridge' -ErrorAction SilentlyContinue
            Get-CimInstance Win32_Process | Where-Object {
                $_.Name -eq 'OpcBridge.App.exe' -or ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*OpcBridge.App.dll*')
            } | ForEach-Object {
                Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            }

            # The old process must be gone before the new one starts: the bridge binds its
            # ports at startup, and a leftover holder would roll the dashboard port.
            for ($i = 0; $i -lt 10; $i++) {
                if ($null -eq (Get-Process -Name 'OpcBridge.App' -ErrorAction SilentlyContinue)) { break }
                Start-Sleep -Seconds 1
            }

            Start-ScheduledTask -TaskName 'OpcBridge'
            $restarted = $true
            Write-Host "    scheduled task 'OpcBridge' restarted (state $((Get-ScheduledTask -TaskName 'OpcBridge').State))"
        }
        else {
            Write-Host "    no OpcBridge service or scheduled task found - restart the bridge yourself to apply the switch"
        }
    }
}

if ($restarted) {
    if ([string]::IsNullOrWhiteSpace($HealthUrl)) {
        $port = 8080
        $appSettings = Join-Path $PublishDir 'appsettings.json'
        try {
            if (Test-Path -LiteralPath $appSettings) {
                $cfg = Get-Content -LiteralPath $appSettings -Raw | ConvertFrom-Json
                if ($cfg.Bridge.HttpPort -gt 0) {
                    $port = [int]$cfg.Bridge.HttpPort
                }
            }
        }
        catch {
            # keep 8080
        }
        $HealthUrl = "http://127.0.0.1:$port/health"
    }

    Write-Host "==> Waiting for $HealthUrl"
    for ($i = 0; $i -lt $ProbeSeconds; $i++) {
        Start-Sleep -Seconds 1
        try {
            $health = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 3
            if ($health.status -eq 'ok') { break }
        }
        catch {
        }
    }

    if ($null -ne $health -and $health.status -eq 'ok') {
        Write-Host "    health : ok"
    }
    else {
        Write-Host "    health : no answer after $ProbeSeconds s - the dashboard port may have been moved (Monitor > Port Configuration) or the bridge is still starting"
    }
}

Write-Host ""
Write-Host "==> Done. Sample this arm with scripts/windows/measure-windows-memory.sh --label $Mode (from Linux) or monitor-opcbridge.ps1 (on this host)."

[pscustomobject]@{
    mode           = $Mode
    runtimeConfig  = $runtimeConfig
    gcServerBefore = $before
    gcServerAfter  = $after
    restarted      = $restarted
    health         = if ($null -ne $health) { $health.status } else { 'unprobed' }
} | ConvertTo-Json -Depth 4 -Compress
