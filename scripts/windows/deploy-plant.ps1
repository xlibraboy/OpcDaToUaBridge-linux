param(
    [string]$RepoRoot = 'C:\Users\Tested1\Documents\OpcBridge'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Plant deploy: S4U headless Session 0 (real PLC, no simulator)
# Usage: powershell -ExecutionPolicy Bypass -File .\scripts\windows\deploy-plant.ps1
# Disables auto-logon for security.

Write-Host "==> Plant deploy: S4U headless Session 0"

# Disable auto-logon (plant security)
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name AutoAdminLogon -Value '0' -Type String -Force
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name DefaultPassword -ErrorAction SilentlyContinue

$scriptRoot = Split-Path -Parent $PSCommandPath
& (Join-Path $scriptRoot 'register-published-task.ps1') -LogonType S4U

Write-Host "Plant task is now S4U AtStartup (Session 0). No logon required. Remove mx1 GX Simulator station from sources.json for plant."
