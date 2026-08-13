<#
.SYNOPSIS
    Reproduces the trimming gate evidence for issue #13.

.DESCRIPTION
    Trimming is ON in production and CI. This script reproduces the mechanical evidence tracked
    by the gate in docs/TRIMMING.md, so the warning inventory and runtime checks can be re-derived
    rather than believed:

      1. An untrimmed ReadyToRun publish of the app  — the shipping configuration, for size.
      2. A trimmed publish of the app  — warning inventory and size.
      3. A trimmed publish of tests/ConnectOnion.TrimSmoke, run as two processes (persist, then
         verify) so a real restart separates writing the turn from restoring it.

    Warnings are classified app-owned vs third-party by where ILLink says they come from: a line
    beginning with a repo source path is ours, while a line beginning with "ILLink :" is generated
    code inside a dependency. CsWinRT's ABI marshalling was the historical example; the currently
    pinned targeting pack emits none. Only the first kind blocks the gate.

    Nothing here changes the build configuration. Any change to the trimming configuration is a
    deliberate, separate edit to ConnectOnion.WinUIClient.csproj, gated on docs/TRIMMING.md.

.PARAMETER OutDir
    Where publishes, logs and the report are written. Defaults to artifacts/trim-audit.

.PARAMETER SkipUntrimmed
    Skip the untrimmed baseline publish. The size comparison is then carried over from the
    previous run's report rather than measured.

.PARAMETER SmokeOnly
    Only build and run the trimmed smoke harness. The fast loop while fixing a trim regression.

.EXAMPLE
    pwsh scripts/Invoke-TrimAudit.ps1
.EXAMPLE
    pwsh scripts/Invoke-TrimAudit.ps1 -SmokeOnly
#>
[CmdletBinding()]
param(
    [string] $OutDir,
    [switch] $SkipUntrimmed,
    [switch] $SmokeOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'artifacts/trim-audit' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$AppProject   = Join-Path $RepoRoot 'ConnectOnion.WinUIClient/ConnectOnion.WinUIClient.csproj'
$SmokeProject = Join-Path $RepoRoot 'tests/ConnectOnion.TrimSmoke/ConnectOnion.TrimSmoke.csproj'

function Get-DirectorySizeMb {
    param([string] $Path)
    if (-not (Test-Path $Path)) { return $null }
    $bytes = (Get-ChildItem -Path $Path -Recurse -File | Measure-Object -Property Length -Sum).Sum
    if (-not $bytes) { return 0 }
    return [math]::Round($bytes / 1MB, 2)
}

<#
    Splits an MSBuild log's IL warnings into the two populations the gate treats differently.

    An app-owned warning names one of our source files and a line number:
        D:\...\ConnectOnion.Protocol\AgentConnectionService.cs(984,20): warning IL2026: ...
    A third-party warning is raised by ILLink against generated code with no source position:
        ILLink : Trim analysis warning IL2081: ABI.Windows.Foundation... [D:\...\Some.csproj]
    The trailing [project] tag is on both, so it cannot be used to tell them apart — the leading
    token is what distinguishes them.
#>
function Split-TrimWarnings {
    param([string] $LogPath)

    $lines = @(Get-Content -LiteralPath $LogPath | Where-Object { $_ -match 'warning IL\d+' })
    $appOwned = @()
    $thirdParty = @()

    foreach ($line in $lines) {
        if ($line -match '^\s*ILLink\s*:') { $thirdParty += $line }
        elseif ($line -match '^\s*[A-Za-z]:\\.*\.cs\(\d+,\d+\):') { $appOwned += $line }
        else { $thirdParty += $line }
    }

    # ILLink and the Roslyn analyzer report the same site, so distinct sites is the honest count.
    $sites = @($appOwned | ForEach-Object {
        if ($_ -match '^(?<file>[A-Za-z]:\\[^(]+)\((?<line>\d+),\d+\):\s*warning\s*(?<id>IL\d+)') {
            "{0}({1}): {2}" -f (Resolve-Path -Relative -LiteralPath $Matches.file -ErrorAction SilentlyContinue), $Matches.line, $Matches.id
        }
    } | Sort-Object -Unique)

    return [pscustomobject]@{
        AppOwnedRaw    = $appOwned.Count
        AppOwnedSites  = $sites
        ThirdPartyRaw  = $thirdParty.Count
        ThirdPartyIds  = @($thirdParty | ForEach-Object {
            if ($_ -match 'warning (IL\d+)') { $Matches[1] }
        } | Group-Object | ForEach-Object { "$($_.Name) x$($_.Count)" } | Sort-Object)
    }
}

function Invoke-Publish {
    param(
        [string] $Project,
        [string] $PublishDir,
        [string] $LogPath,
        [string[]] $ExtraArgs
    )

    if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }

    $publishArgs = @(
        'publish', $Project,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '-p:AppxPackageSigningEnabled=false',
        '-o', $PublishDir
    ) + $ExtraArgs

    Write-Host "  dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
    & dotnet @publishArgs *>&1 | Tee-Object -FilePath $LogPath | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Write-Host "  publish FAILED — see $LogPath" -ForegroundColor Red
        throw "Publish failed for $Project (exit $LASTEXITCODE)."
    }
}

$report = [ordered]@{
    capturedUtc = (Get-Date).ToUniversalTime().ToString('o')
    machine     = $env:COMPUTERNAME
    sdk         = (& dotnet --version)
    commit      = (& git -C $RepoRoot rev-parse --short HEAD)
}

# --- 1. Untrimmed baseline (the shipping configuration) -------------------------------------

if (-not $SmokeOnly -and -not $SkipUntrimmed) {
    Write-Host "`n[1/3] Untrimmed ReadyToRun publish (the shipping configuration)" -ForegroundColor Cyan
    $dir = Join-Path $OutDir 'untrimmed'
    Invoke-Publish -Project $AppProject -PublishDir $dir `
        -LogPath (Join-Path $OutDir 'untrimmed-publish.log') `
        -ExtraArgs @('-p:Platform=x64', '-p:RunUnpackaged=true', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=true')

    $report.untrimmedSizeMb = Get-DirectorySizeMb $dir
    Write-Host ("  size: {0} MB" -f $report.untrimmedSizeMb)
}

# --- 2. Trimmed warning-inventory publish ----------------------------------------------------

if (-not $SmokeOnly) {
    Write-Host "`n[2/3] Trimmed publish (warning inventory)" -ForegroundColor Cyan
    $dir = Join-Path $OutDir 'trimmed'
    $log = Join-Path $OutDir 'trimmed-publish.log'
    Invoke-Publish -Project $AppProject -PublishDir $dir -LogPath $log `
        -ExtraArgs @(
            '-p:Platform=x64', '-p:RunUnpackaged=true',
            '-p:PublishTrimmed=true', '-p:PublishReadyToRun=false',
            '-p:SuppressTrimAnalysisWarnings=false', '-p:TrimmerSingleWarn=false')

    $warnings = Split-TrimWarnings -LogPath $log
    $report.trimmedSizeMb        = Get-DirectorySizeMb $dir
    $report.appOwnedWarningSites = @($warnings.AppOwnedSites)
    $report.thirdPartyWarnings   = @($warnings.ThirdPartyIds)

    Write-Host ("  size: {0} MB" -f $report.trimmedSizeMb)
    Write-Host ("  app-owned warning sites: {0}" -f $warnings.AppOwnedSites.Count) -ForegroundColor (
        $(if ($warnings.AppOwnedSites.Count -eq 0) { 'Green' } else { 'Red' }))
    foreach ($site in $warnings.AppOwnedSites) { Write-Host "    $site" -ForegroundColor Red }
    Write-Host ("  third-party (not gating): {0}" -f ($warnings.ThirdPartyIds -join ', '))
}

# --- 3. Trimmed runtime smoke, across a real restart -----------------------------------------

Write-Host "`n[3/3] Trimmed smoke harness (persist, restart, verify)" -ForegroundColor Cyan
$smokeDir = Join-Path $OutDir 'smoke'
Invoke-Publish -Project $SmokeProject -PublishDir $smokeDir `
    -LogPath (Join-Path $OutDir 'smoke-publish.log') `
    -ExtraArgs @('-p:NuGetLockFilePath=packages.trim-smoke.lock.json')

$smokeExe = Join-Path $smokeDir 'ConnectOnion.TrimSmoke.exe'
if (-not (Test-Path $smokeExe)) { throw "Smoke harness was not published to $smokeExe." }

# One data root shared by both processes; the restart between them is the point.
$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ConnectOnion.TrimSmoke." + [guid]::NewGuid().ToString('N'))
$env:CONNECTONION_TRIMSMOKE_ROOT = $smokeRoot
try {
    & $smokeExe persist | Tee-Object -FilePath (Join-Path $OutDir 'smoke-persist.log')
    $persistExit = $LASTEXITCODE
    & $smokeExe verify  | Tee-Object -FilePath (Join-Path $OutDir 'smoke-verify.log')
    $verifyExit = $LASTEXITCODE
}
finally {
    Remove-Item Env:\CONNECTONION_TRIMSMOKE_ROOT -ErrorAction SilentlyContinue
    if (Test-Path $smokeRoot) { Remove-Item -Recurse -Force $smokeRoot -ErrorAction SilentlyContinue }
}

$report.smokePersistPassed = ($persistExit -eq 0)
$report.smokeVerifyPassed  = ($verifyExit -eq 0)

# --- Report -----------------------------------------------------------------------------------

if ($report.Contains('untrimmedSizeMb') -and $report.Contains('trimmedSizeMb') -and $report.untrimmedSizeMb) {
    $report.sizeSavingMb = [math]::Round($report.untrimmedSizeMb - $report.trimmedSizeMb, 2)
    $report.sizeSavingPercent = [math]::Round(
        100 * ($report.untrimmedSizeMb - $report.trimmedSizeMb) / $report.untrimmedSizeMb, 1)
}

$reportPath = Join-Path $OutDir 'trim-audit.json'
$report | ConvertTo-Json -Depth 6 | Out-File -FilePath $reportPath -Encoding utf8
Write-Host "`nReport: $reportPath" -ForegroundColor Cyan

$gateBlockers = @()
if ($report.Contains('appOwnedWarningSites') -and $report.appOwnedWarningSites.Count -gt 0) {
    $gateBlockers += "$($report.appOwnedWarningSites.Count) app-owned trim warning site(s)"
}
if (-not $report.smokePersistPassed) { $gateBlockers += 'trimmed smoke persist phase failed' }
if (-not $report.smokeVerifyPassed)  { $gateBlockers += 'trimmed smoke verify phase failed' }

if ($gateBlockers.Count -gt 0) {
    Write-Host ("GATE BLOCKED: " + ($gateBlockers -join '; ')) -ForegroundColor Red
    exit 1
}

# Deliberately not "gate passed". This script covers the mechanical criteria only; the signed
# MSIX matrix, the real-window smoke test and the performance comparison are separate, and
# docs/TRIMMING.md tracks all of them.
Write-Host "Mechanical criteria passed. See docs/TRIMMING.md for the criteria this cannot cover." -ForegroundColor Green
exit 0
