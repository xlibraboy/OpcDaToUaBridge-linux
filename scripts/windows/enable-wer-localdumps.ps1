param(
    [string[]]$ProcessName = @('OpcBridge.App.exe'),

    [string]$DumpFolder = 'C:\ProgramData\OpcBridge\dumps',

    [ValidateSet('Mini', 'Full')]
    [string]$DumpType = 'Full',

    [ValidateRange(1, 50)]
    [int]$DumpCount = 5,

    [string]$GrantUser = '',

    [switch]$Remove
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Captures native crash dumps for the bridge (and the COM surrogate that can host a
# faulting in-proc server, DllHost.exe).
#
# Why: the Honeywell PMD in-proc OPC DA server kills its host process about a minute into
# every subscription lifecycle, and leaves NO crash-*.log behind — the process is killed
# natively, so the managed crash handler never runs. Windows Error Reporting LocalDumps
# writes the dump at the OS level, which is the only way to get the native stack.
#
# The configuration is per-executable and machine-wide:
#   HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\<exe>
#     DumpFolder  REG_EXPAND_SZ  where dumps land
#     DumpType    REG_DWORD      1 = mini, 2 = full
#     DumpCount   REG_DWORD      how many dumps to keep per executable
#
# Usage (elevated):
#   powershell -ExecutionPolicy Bypass -File .\enable-wer-localdumps.ps1
#   powershell -ExecutionPolicy Bypass -File .\enable-wer-localdumps.ps1 -ProcessName OpcBridge.App.exe,DllHost.exe
#   powershell -ExecutionPolicy Bypass -File .\enable-wer-localdumps.ps1 -GrantUser '.\mesadm1'
#   powershell -ExecutionPolicy Bypass -File .\enable-wer-localdumps.ps1 -Remove
#
# Analyse a dump with WinDbg:  windbg -z <dump>   then   !analyze -v   and   lm / k
# Procedure and expected results: docs/pmd-opc-da-field-notes-2026-09-30.md.

$dumpTypeValues = @{ Mini = 1; Full = 2 }
$dumpTypeValue = $dumpTypeValues[$DumpType]
$baseKey = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell: WER LocalDumps lives under HKLM.'
}

Write-Host "==> WER LocalDumps ($(if ($Remove) { 'remove' } else { 'apply' }))"
$applied = @()

if ($Remove) {
    foreach ($name in $ProcessName) {
        $key = "$baseKey\$name"
        if (Test-Path -LiteralPath $key) {
            Remove-Item -LiteralPath $key -Recurse -Force
            Write-Host "    removed : $name"
            $applied += $name
        }
        else {
            Write-Host "    absent  : $name (nothing to remove)"
        }
    }

    Write-Host ""
    Write-Host "==> LocalDumps configuration removed. Existing dumps in $DumpFolder are left in place."
}
else {
    if (-not (Test-Path -LiteralPath $DumpFolder)) {
        New-Item -Path $DumpFolder -ItemType Directory -Force | Out-Null
        Write-Host "    folder  : created $DumpFolder"
    }
    else {
        Write-Host "    folder  : exists $DumpFolder"
    }

    if (-not [string]::IsNullOrWhiteSpace($GrantUser)) {
        & icacls $DumpFolder /grant "${GrantUser}:(OI)(CI)M" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "icacls failed to grant '$GrantUser' modify access to $DumpFolder (exit $LASTEXITCODE)."
        }
        Write-Host "    acl     : granted '$GrantUser' modify on $DumpFolder"
    }

    foreach ($name in $ProcessName) {
        $key = "$baseKey\$name"
        New-Item -Path $key -Force | Out-Null
        New-ItemProperty -LiteralPath $key -Name 'DumpFolder' -PropertyType ExpandString -Value $DumpFolder -Force | Out-Null
        New-ItemProperty -LiteralPath $key -Name 'DumpType' -PropertyType DWord -Value $dumpTypeValue -Force | Out-Null
        New-ItemProperty -LiteralPath $key -Name 'DumpCount' -PropertyType DWord -Value $DumpCount -Force | Out-Null
        Write-Host "    $name -> $DumpFolder (type $DumpType, keep $DumpCount)"
        $applied += $name
    }
}

$verified = $true
$states = @()
foreach ($name in $applied) {
    $key = "$baseKey\$name"
    $present = Test-Path -LiteralPath $key
    $folderValue = $null
    $typeValue = $null
    $countValue = $null
    if ($present) {
        $registryKey = Get-Item -LiteralPath $key
        $folderValue = [string]$registryKey.GetValue('DumpFolder')
        $typeValue = $registryKey.GetValue('DumpType')
        $countValue = $registryKey.GetValue('DumpCount')
        if (-not $Remove -and ($folderValue -ne $DumpFolder -or [int]$typeValue -ne $dumpTypeValue -or [int]$countValue -ne $DumpCount)) {
            $verified = $false
        }
    }

    $states += [pscustomobject]@{
        processName = $name
        present     = $present
        dumpFolder  = $folderValue
        dumpType    = $typeValue
        dumpCount   = $countValue
    }
}

if (-not $verified) {
    throw 'Wrote the LocalDumps keys but the read-back does not match.'
}

if (-not $Remove) {
    Write-Host ""
    Write-Host "==> Dumps land in $DumpFolder for: $($ProcessName -join ', ')"
    Write-Host "    A native crash has no crash-*.log - the dump is the evidence."
    Write-Host "    WinDbg: windbg -z <dump>  then  !analyze -v  and  lm / k"
}

[pscustomobject]@{
    action       = if ($Remove) { 'remove' } else { 'apply' }
    processNames = @($ProcessName)
    dumpFolder   = $DumpFolder
    dumpType     = $DumpType
    dumpCount    = $DumpCount
    grantUser    = $GrantUser
    applied      = @($applied)
    states       = @($states)
    verified     = $verified
} | ConvertTo-Json -Depth 5 -Compress
