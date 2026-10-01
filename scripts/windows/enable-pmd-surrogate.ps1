param(
    [string]$ProgId = 'PMD.DDT_OPCDataServer.1',

    [string]$RunAsAccount = '.\mesadm1',

    [string]$RunAsPassword = '',

    [ValidateSet('32', '64')]
    [string]$RegistryView = '32',

    [string]$RollbackDir = 'C:\ProgramData\OpcBridge\rollback',

    [switch]$Force,

    [switch]$Rollback
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Runs a faulting IN-PROC OPC DA server out of process in the system COM surrogate.
#
# Why: a server registered with InprocServer32 loads its DLL into the CLIENT process, so a
# fault in that DLL kills the whole bridge. Honeywell PMD (PMD.DDT_OPCDataServer.1) is such
# a server and faults about a minute into every OPC DA subscription lifecycle (see
# docs/pmd-opc-da-field-notes-2026-09-30.md). Setting DllSurrogate on the server's AppID
# makes COM activate it inside dllhost.exe instead, so the same fault kills only the
# surrogate - the bridge survives. RunAs sets the identity the surrogate runs as, which this
# PMD/DCOM stack requires to be a plant account (e.g. .\mesadm1, member of Distributed COM
# Users), never LocalSystem/Administrator.
#
# The change is registry-only, per CLSID/AppID and per registry view:
#   <view>\CLSID\{PMD-CLSID}:  AppID = {appid}          (created if the CLSID had none)
#   <view>\AppID\{appid}:      DllSurrogate = ""        (empty = the system surrogate, dllhost.exe)
#                              RunAs        = ".\mesadm1,<password>"
# The x86 bridge reads the 32-bit view (HKLM\SOFTWARE\Classes\Wow6432Node), which is the
# default here; a 64-bit-only host uses -RegistryView 64.
#
# A rollback record (registry export + JSON of the previous values) is written under
# C:\ProgramData\OpcBridge\rollback BEFORE anything changes; -Rollback restores it.
#
# Usage (elevated, 64-bit PowerShell):
#   powershell -ExecutionPolicy Bypass -File .\enable-pmd-surrogate.ps1 -RunAsPassword '<pw>'
#   powershell -ExecutionPolicy Bypass -File .\enable-pmd-surrogate.ps1 -ProgId 'Vendor.Server.1' -RunAsAccount '.\opcu1' -RunAsPassword '<pw>'
#   powershell -ExecutionPolicy Bypass -File .\enable-pmd-surrogate.ps1 -Rollback
#
# Verification (see the field notes):
#   1. Ops > Troubleshoot > activation probe succeeds.
#   2. (Get-Process dllhost).Modules | Where-Object ModuleName -match 'PMD' lists the vendor DLL.
#   3. Run the source with subscriptions for > 5 minutes: if dllhost dies/restarts while the
#      bridge PID and /health stay unchanged, containment is proven.
#
# Caveats: not every in-proc server tolerates surrogate isolation (window station, licensing,
# single-instance assumptions), and IOPCDataCallback callbacks now cross a marshalling
# boundary. If PMD misbehaves, roll back and use the worker-process design instead.

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell: the change lives under HKLM.'
}
if (-not [Environment]::Is64BitProcess) {
    throw 'Run this from 64-bit PowerShell: the script addresses the Wow6432Node / native registry views explicitly.'
}

function Get-RegistryValue([string]$KeyPath, [string]$Name) {
    if (-not (Test-Path -LiteralPath $KeyPath)) {
        return $null
    }

    return (Get-Item -LiteralPath $KeyPath).GetValue($Name)
}

function Get-BatchLogonState([string]$Account) {
    try {
        $sid = (New-Object System.Security.Principal.NTAccount($Account)).Translate([System.Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        return [pscustomobject]@{ state = 'Unknown'; detail = "account '$Account' does not resolve on this host" }
    }

    $cfg = Join-Path $env:TEMP ('opcbridge-secedit-' + [guid]::NewGuid().ToString('N') + '.cfg')
    try {
        & secedit /export /cfg $cfg /areas USER_RIGHTS | Out-Null
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{ state = 'Unknown'; detail = "secedit exited $LASTEXITCODE" }
        }

        $line = Select-String -Path $cfg -Pattern '^SeBatchLogonRight\s*=' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $line) {
            return [pscustomobject]@{ state = 'Missing'; detail = 'SeBatchLogonRight is not assigned to any account' }
        }

        if ($line.Line -match [regex]::Escape($sid)) {
            return [pscustomobject]@{ state = 'Present'; detail = $sid }
        }

        return [pscustomobject]@{ state = 'Missing'; detail = "policy lists no $sid (group membership is not resolved here)" }
    }
    catch {
        return [pscustomobject]@{ state = 'Unknown'; detail = $_.Exception.Message }
    }
    finally {
        Remove-Item -LiteralPath $cfg -Force -ErrorAction SilentlyContinue
    }
}

$providerRoot = if ($RegistryView -eq '32') { 'HKLM:\SOFTWARE\Classes\Wow6432Node' } else { 'HKLM:\SOFTWARE\Classes' }
$regRoot = if ($RegistryView -eq '32') { 'HKLM\SOFTWARE\Classes\Wow6432Node' } else { 'HKLM\SOFTWARE\Classes' }

if ($Rollback) {
    if (-not (Test-Path -LiteralPath $RollbackDir)) {
        throw "No rollback directory at $RollbackDir."
    }

    $recordFile = Get-ChildItem -LiteralPath $RollbackDir -Filter 'appid-*.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $recordFile) {
        throw "No appid-*.json rollback record in $RollbackDir."
    }

    $record = Get-Content -LiteralPath $recordFile.FullName -Raw | ConvertFrom-Json
    $providerRoot = if ($record.registryView -eq '64') { 'HKLM:\SOFTWARE\Classes' } else { 'HKLM:\SOFTWARE\Classes\Wow6432Node' }
    $appIdKey = Join-Path $providerRoot "AppID\$($record.appId)"
    $clsidKey = Join-Path $providerRoot "CLSID\$($record.clsid)"

    Write-Host "==> Rolling back $($record.progId) (AppID $($record.appId)) from $($recordFile.Name)"

    if ($record.appIdKeyExisted) {
        if ($record.dllSurrogateExisted) {
            New-ItemProperty -LiteralPath $appIdKey -Name 'DllSurrogate' -PropertyType String -Value ([string]$record.dllSurrogateValue) -Force | Out-Null
        }
        else {
            Remove-ItemProperty -LiteralPath $appIdKey -Name 'DllSurrogate' -ErrorAction SilentlyContinue
        }

        if ($record.runAsExisted) {
            New-ItemProperty -LiteralPath $appIdKey -Name 'RunAs' -PropertyType String -Value ([string]$record.runAsValue) -Force | Out-Null
        }
        else {
            Remove-ItemProperty -LiteralPath $appIdKey -Name 'RunAs' -ErrorAction SilentlyContinue
        }
    }
    elseif (Test-Path -LiteralPath $appIdKey) {
        Remove-Item -LiteralPath $appIdKey -Recurse -Force
    }

    if ($record.appIdValueExisted) {
        New-ItemProperty -LiteralPath $clsidKey -Name 'AppID' -PropertyType String -Value ([string]$record.appIdValue) -Force | Out-Null
    }
    else {
        Remove-ItemProperty -LiteralPath $clsidKey -Name 'AppID' -ErrorAction SilentlyContinue
    }

    Write-Host "    restored - the source must reconnect (or the bridge restart) to stop using the surrogate"
    Write-Host ""
    [pscustomobject]@{
        action       = 'rollback'
        progId       = $record.progId
        clsid        = $record.clsid
        appId        = $record.appId
        registryView = $record.registryView
        record       = $recordFile.FullName
    } | ConvertTo-Json -Depth 4 -Compress
    return
}

Write-Host "==> Resolving $ProgId in the $RegistryView-bit registry view"
$progKey = Join-Path $providerRoot $ProgId
$clsid = [string](Get-RegistryValue $progKey '')
if ([string]::IsNullOrWhiteSpace($clsid)) {
    throw "ProgID '$ProgId' is not registered in this view ($progKey)."
}
if ($clsid -notmatch '^\{[0-9A-Fa-f\-]{36}\}$') {
    throw "The ProgID's default value is not a CLSID: '$clsid'."
}

$clsidKey = Join-Path $providerRoot "CLSID\$clsid"
if (-not (Test-Path -LiteralPath $clsidKey)) {
    throw "CLSID $clsid is not registered in this view."
}

$localServer = [string](Get-RegistryValue $clsidKey 'LocalServer32')
$inprocServer = [string](Get-RegistryValue $clsidKey 'InprocServer32')
if (-not [string]::IsNullOrWhiteSpace($localServer) -and -not $Force) {
    throw "CLSID $clsid already registers LocalServer32 ('$localServer') - it runs out of process and must not be surrogated. Use -Force to override."
}
if ([string]::IsNullOrWhiteSpace($inprocServer)) {
    Write-Warning "CLSID $clsid has no InprocServer32 - this script is meant for in-proc servers."
}

$appId = [string](Get-RegistryValue $clsidKey 'AppID')
$appIdValueExisted = -not [string]::IsNullOrWhiteSpace($appId)
if (-not $appIdValueExisted) {
    $appId = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'
}

$appIdKey = Join-Path $providerRoot "AppID\$appId"
$appIdKeyExisted = Test-Path -LiteralPath $appIdKey
$appIdRegPath = "$regRoot\AppID\$appId"
$clsidRegPath = "$regRoot\CLSID\$clsid"

Write-Host "    CLSID : $clsid"
Write-Host "    AppID : $appId $(if ($appIdValueExisted) { '(existing)' } else { '(new - will be written to the CLSID)' })"

if ([string]::IsNullOrEmpty($RunAsPassword)) {
    Write-Host ""
    Write-Host "==> RunAsPassword is required to set the COM identity. Nothing was changed."
    Write-Host "    Manual steps:"
    if (-not $appIdKeyExisted) {
        Write-Host ('      reg add "{0}" /f' -f $appIdRegPath)
    }
    if (-not $appIdValueExisted) {
        Write-Host ('      reg add "{0}" /v AppID /t REG_SZ /d "{1}" /f' -f $clsidRegPath, $appId)
    }
    Write-Host ('      reg add "{0}" /v DllSurrogate /t REG_SZ /d "" /f' -f $appIdRegPath)
    Write-Host ('      reg add "{0}" /v RunAs /t REG_SZ /d "{1},<password>" /f' -f $appIdRegPath, $RunAsAccount)
    [pscustomobject]@{
        ok           = $false
        action       = 'manual'
        progId       = $ProgId
        clsid        = $clsid
        appId        = $appId
        registryView = $RegistryView
        reason       = 'RunAsPassword not provided'
    } | ConvertTo-Json -Depth 4 -Compress
    exit 2
}

$existingKey = if ($appIdKeyExisted) { Get-Item -LiteralPath $appIdKey } else { $null }
$dllSurrogateExisted = $appIdKeyExisted -and ($null -ne $existingKey.GetValue('DllSurrogate'))
$dllSurrogateValue = if ($dllSurrogateExisted) { [string]$existingKey.GetValue('DllSurrogate') } else { '' }
$runAsExisted = $appIdKeyExisted -and ($null -ne $existingKey.GetValue('RunAs'))
$runAsValue = if ($runAsExisted) { [string]$existingKey.GetValue('RunAs') } else { '' }

New-Item -Path $RollbackDir -ItemType Directory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$safeId = $appId.Trim('{', '}')
$regFile = Join-Path $RollbackDir "appid-$safeId-$stamp.reg"
$recordFile = Join-Path $RollbackDir "appid-$safeId-$stamp.json"

$exported = $false
if ($appIdKeyExisted) {
    & reg export $appIdRegPath $regFile /y | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "reg export failed (exit $LASTEXITCODE) - aborting before any change."
    }
    $exported = $true
}

[pscustomobject]@{
    progId              = $ProgId
    clsid               = $clsid
    appId               = $appId
    registryView        = $RegistryView
    appIdKeyExisted     = $appIdKeyExisted
    appIdValueExisted   = $appIdValueExisted
    appIdValue          = $appId
    dllSurrogateExisted = $dllSurrogateExisted
    dllSurrogateValue   = $dllSurrogateValue
    runAsExisted        = $runAsExisted
    runAsValue          = $runAsValue
    runAsAccount        = $RunAsAccount
    exported            = $exported
    timestamp           = (Get-Date).ToString('o')
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $recordFile -Encoding UTF8

Write-Host "==> $ProgId -> surrogate (dllhost.exe) as $RunAsAccount"
New-Item -Path $appIdKey -Force | Out-Null
New-ItemProperty -LiteralPath $appIdKey -Name 'DllSurrogate' -PropertyType String -Value '' -Force | Out-Null
New-ItemProperty -LiteralPath $appIdKey -Name 'RunAs' -PropertyType String -Value "$RunAsAccount,$RunAsPassword" -Force | Out-Null
if (-not $appIdValueExisted) {
    New-ItemProperty -LiteralPath $clsidKey -Name 'AppID' -PropertyType String -Value $appId -Force | Out-Null
}

$verifyKey = Get-Item -LiteralPath $appIdKey
$surrogateCheck = $verifyKey.GetValue('DllSurrogate')
$runAsCheck = [string]$verifyKey.GetValue('RunAs')
if ($null -eq $surrogateCheck) {
    throw 'DllSurrogate did not read back - the AppID key was not updated.'
}
if ($runAsCheck -ne "$RunAsAccount,$RunAsPassword") {
    throw 'RunAs did not read back - the AppID key was not updated.'
}
if (-not $appIdValueExisted -and ([string](Get-RegistryValue $clsidKey 'AppID') -ne $appId)) {
    throw 'The AppID value did not read back on the CLSID key.'
}

$batchLogon = Get-BatchLogonState $RunAsAccount
if ($batchLogon.state -eq 'Missing') {
    Write-Warning "Account '$RunAsAccount' does not appear in SeBatchLogonRight; the surrogate will fail to launch. Grant 'Log on as a batch job' in secpol.msc > Local Policies > User Rights Assignment."
}
elseif ($batchLogon.state -eq 'Unknown') {
    Write-Warning "Could not check SeBatchLogonRight for '$RunAsAccount': $($batchLogon.detail)"
}

Write-Host "    DllSurrogate : '' (system surrogate)"
Write-Host "    RunAs        : $RunAsAccount"
Write-Host "    read-back    : verified"
Write-Host "    rollback     : $recordFile"
Write-Host ""
Write-Host "==> A fault in the vendor DLL now kills dllhost, not the bridge."
Write-Host "    The running bridge keeps its existing session until the source reconnects (or the bridge restarts)."
Write-Host "    Verify with: (Get-Process dllhost).Modules | Where-Object ModuleName -match 'PMD'"

[pscustomobject]@{
    ok           = $true
    action       = 'apply'
    progId       = $ProgId
    clsid        = $clsid
    appId        = $appId
    registryView = $RegistryView
    created      = @{
        appIdKey   = -not $appIdKeyExisted
        appIdValue = -not $appIdValueExisted
    }
    dllSurrogate = 'system (dllhost.exe)'
    runAsAccount = $RunAsAccount
    batchLogon   = $batchLogon.state
    rollback     = $recordFile
    verified     = $true
} | ConvertTo-Json -Depth 5 -Compress
