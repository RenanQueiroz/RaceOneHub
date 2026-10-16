<#
.SYNOPSIS
Fast-forwards this checkout to the fork's main branch, then runs fork\setup.ps1.

.DESCRIPTION
Only the fork (origin) is followed; upstream changes reach it only when merged
into the fork on purpose (FORK.md). Local edits are carried across the merge
with --autostash. The Hub runs this with -SkipHub -ProgressFile and, when the
result says hub_changed, restarts itself through setup.ps1 -HubOnly -WaitForPid.
#>
[CmdletBinding()]
param(
    [switch]$SkipHub,
    [string]$ProgressFile
)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot

function Write-Result([hashtable]$Value) {
    if (-not $ProgressFile) { return }
    $temporary = $ProgressFile + '.tmp'
    [IO.File]::WriteAllText($temporary, (ConvertTo-Json $Value -Compress), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $ProgressFile -Force
}

function Invoke-Git([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & git -C $Root @Arguments 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw ('git ' + $Arguments[0] + ' failed: ' + ($output -join ' ')) }
    return $output
}

try {
    $env:GIT_TERMINAL_PROMPT = '0'
    Write-Result @{ step = 'fetch'; message = 'Fetching the fork'; index = 0; total = 0; done = $false }
    $branch = (Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')) -join ''
    if ($branch -ne 'main') { throw "This checkout is on '$branch'; updates follow main only." }
    Invoke-Git @('fetch', '--quiet', 'origin') | Out-Null
    $before = (Invoke-Git @('rev-parse', 'HEAD')) -join ''
    Write-Result @{ step = 'merge'; message = 'Updating to the fork''s latest commit'; index = 0; total = 0; done = $false }
    Invoke-Git @('merge', '--ff-only', '--autostash', 'origin/main') | Write-Host
    $after = (Invoke-Git @('rev-parse', 'HEAD')) -join ''
    Write-Host "Updated $($before.Substring(0, 7)) -> $($after.Substring(0, 7))"
} catch {
    Write-Result @{ done = $true; ok = $false; error = $_.Exception.Message }
    Write-Host "Update failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

$setup = @{ ProgressFile = $ProgressFile }
if ($SkipHub) { $setup.SkipHub = $true }
& (Join-Path $PSScriptRoot 'setup.ps1') @setup
exit $LASTEXITCODE
