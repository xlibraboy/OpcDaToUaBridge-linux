param(
    [string]$RepoRoot = 'C:\Users\Tested1\Documents\OpcBridge'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Lab deploy: Interactive Hidden + auto-logon for GX Simulator (session-bound)
# Usage: powershell -ExecutionPolicy Bypass -File .\scripts\windows\deploy-lab.ps1
# Requires Tested1 password '19891989' for auto-logon (lab VM only).

Write-Host "==> Lab deploy: Interactive Hidden (GX Simulator)"

# Ensure auto-logon (lab only)
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name AutoAdminLogon -Value '1' -Type String -Force
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name DefaultUserName -Value 'Tested1' -Type String -Force
# Plaintext lab password — do not use for plant
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name DefaultPassword -Value '19891989' -Type String -Force
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name DefaultDomainName -Value $env:COMPUTERNAME -Type String -Force

$scriptRoot = Split-Path -Parent $PSCommandPath
& (Join-Path $scriptRoot 'register-published-task.ps1') -LogonType Interactive

Write-Host "Lab task is now Interactive Hidden AtStartup+AtLogOn (Session 1). Reboot will auto-logon Tested1 and start bridge."
