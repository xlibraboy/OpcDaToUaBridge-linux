Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptRoot)
$publishDir = Join-Path $repoRoot 'publish'
$stdout = Join-Path $publishDir 'bridge-task-stdout.log'
$stderr = Join-Path $publishDir 'bridge-task-stderr.log'

[pscustomobject]@{
    stdout = if (Test-Path $stdout) { Get-Content $stdout -Raw } else { '' }
    stderr = if (Test-Path $stderr) { Get-Content $stderr -Raw } else { '' }
} | ConvertTo-Json -Compress
