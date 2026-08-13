<#
.SYNOPSIS
    Drives and records the packaged install / upgrade / uninstall validation for a release.

.DESCRIPTION
    The parts of release validation that need a real installed package on a real Windows profile.
    Everything that can be proved headlessly already is — see
    tests/ConnectOnion.IntegrationTests/Database/ReleaseUpgradeTests.cs, which covers the data and
    identity half against real SQLite and real DPAPI. What is left genuinely requires an install:
    that the package registers, that an upgrade keeps the same LocalState folder, and that the
    shell integrations (toast activation, tray restore) survive it.

    This script automates the evidence and prompts for the judgement. It does not pretend the
    human checkpoints are automated: it stops, tells you exactly what to do, and records what you
    saw. The output is the artifact you attach to the release checklist.

    RUN THIS ON A CLEAN PROFILE, NOT YOUR DEV MACHINE. A machine that has ever had the app
    deployed from Visual Studio already has a LocalState folder, so "the upgrade preserved my
    data" proves nothing there.

.PARAMETER OldPackage
    The previously released .msix, installed first. Omit to validate a fresh install only.

.PARAMETER NewPackage
    The .msix under test.

.PARAMETER IdentityName
    Package identity name. Must match Package.appxmanifest; the default is the production value.

.PARAMETER OutDir
    Where the evidence log is written. Defaults to artifacts/release-validation.

.PARAMETER NonInteractive
    Skip the human checkpoints and record them as "not verified". For collecting the automated
    evidence only — it does NOT constitute a passed validation.

.EXAMPLE
    pwsh scripts/Test-ReleaseUpgrade.ps1 -OldPackage .\v1.0.0.msix -NewPackage .\v1.1.0.msix
#>
[CmdletBinding()]
param(
    [string] $OldPackage,

    [Parameter(Mandatory = $true)]
    [string] $NewPackage,

    [string] $IdentityName = 'ConnectOnion.Desktop',
    [string] $OutDir,
    [switch] $NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'artifacts/release-validation' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$evidence = [System.Collections.Generic.List[object]]::new()
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Evidence {
    param([string] $Step, [string] $Detail, [string] $Result = 'INFO')
    $evidence.Add([pscustomobject]@{
        Time = (Get-Date).ToString('HH:mm:ss'); Step = $Step; Result = $Result; Detail = $Detail
    })
    $color = switch ($Result) { 'PASS' { 'Green' } 'FAIL' { 'Red' } 'MANUAL' { 'Cyan' } default { 'Gray' } }
    Write-Host ("[{0,-6}] {1} — {2}" -f $Result, $Step, $Detail) -ForegroundColor $color
    if ($Result -eq 'FAIL') { $failures.Add("$Step`: $Detail") }
}

function Get-InstalledPackage {
    Get-AppxPackage -Name $IdentityName -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Get-LocalStatePath {
    $package = Get-InstalledPackage
    if (-not $package) { return $null }
    # The publisher hash in the folder name is derived from the Publisher DN, so this resolves it
    # rather than guessing — and a changed Publisher shows up here as a different folder, which is
    # precisely the upgrade-identity failure worth catching.
    return Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState\ConnectOnion"
}

function Get-DataFingerprint {
    <#
        Hashes the user's data so "the installer did not touch it" becomes checkable. Taken while
        the app is closed: WAL and shared-memory sidecars move constantly under a running app, and
        including them would make every comparison fail for reasons that are not about upgrade
        safety. The .db file alone is the durable state.
    #>
    param([string] $LocalState)
    if (-not $LocalState -or -not (Test-Path $LocalState)) { return $null }
    $db = Join-Path $LocalState 'connectonion.db'
    if (-not (Test-Path $db)) { return $null }
    return [pscustomobject]@{
        Path   = $db
        Sha256 = (Get-FileHash $db -Algorithm SHA256).Hash
        Bytes  = (Get-Item $db).Length
    }
}

function Confirm-Manually {
    param([string] $Step, [string] $Instruction)
    if ($NonInteractive) {
        Add-Evidence $Step "NOT VERIFIED (-NonInteractive): $Instruction" 'MANUAL'
        return
    }
    Write-Host "`n  ACTION: $Instruction" -ForegroundColor Cyan
    $answer = Read-Host "  Did it behave as described? [y/N/skip]"
    switch -Regex ($answer) {
        '^(y|yes)$'  { Add-Evidence $Step $Instruction 'PASS' }
        '^(s|skip)$' { Add-Evidence $Step "skipped: $Instruction" 'MANUAL' }
        default {
            $why = Read-Host "  What happened instead?"
            Add-Evidence $Step "$Instruction -> $why" 'FAIL'
        }
    }
}

Write-Host "ConnectOnion release validation`n" -ForegroundColor White

# --- preconditions ------------------------------------------------------------------------------

foreach ($path in @($OldPackage, $NewPackage)) {
    if ($path -and -not (Test-Path $path)) { throw "Package not found: $path" }
}

$existing = Get-InstalledPackage
if ($existing) {
    Add-Evidence 'Preconditions' "$IdentityName $($existing.Version) is already installed — this is not a clean profile" 'FAIL'
    Write-Host "`nRemove it first:  Remove-AppxPackage $($existing.PackageFullName)" -ForegroundColor Yellow
    Write-Host "Note that removing it also deletes its LocalState, including the agent identity." -ForegroundColor Yellow
    exit 1
}
Add-Evidence 'Preconditions' "no existing $IdentityName install" 'PASS'

# --- 1. install the previous release --------------------------------------------------------

$fingerprintBefore = $null

if ($OldPackage) {
    Add-Evidence 'Install (old)' "installing $(Split-Path -Leaf $OldPackage)"
    Add-AppxPackage -Path (Resolve-Path $OldPackage)
    $old = Get-InstalledPackage
    if (-not $old) { Add-Evidence 'Install (old)' 'package did not register' 'FAIL'; exit 1 }
    Add-Evidence 'Install (old)' "registered version $($old.Version)" 'PASS'

    Confirm-Manually 'First launch (old)' `
        'Launch ConnectOnion Desktop from the Start menu, let it finish starting, add an agent or open a conversation so there is data, then close it completely (tray > Exit).'

    $localState = Get-LocalStatePath
    $fingerprintBefore = Get-DataFingerprint $localState
    if ($fingerprintBefore) {
        Add-Evidence 'Baseline data' "connectonion.db $($fingerprintBefore.Bytes) bytes, sha256 $($fingerprintBefore.Sha256.Substring(0,16))..." 'PASS'
    } else {
        Add-Evidence 'Baseline data' "no database under $localState — the old version never wrote data, so the upgrade check cannot mean anything" 'FAIL'
    }

    Confirm-Manually 'Identity (old)' `
        'Reopen the app, go to Settings > Identity, and write down the 0x address and whether a recovery phrase is offered. Then close the app completely.'
}

# --- 2. upgrade ------------------------------------------------------------------------------

$stepName = if ($OldPackage) { 'Upgrade' } else { 'Install' }
Add-Evidence $stepName "installing $(Split-Path -Leaf $NewPackage)"
Add-AppxPackage -Path (Resolve-Path $NewPackage)

$new = Get-InstalledPackage
if (-not $new) { Add-Evidence $stepName 'package did not register' 'FAIL'; exit 1 }
Add-Evidence $stepName "registered version $($new.Version)" 'PASS'

if ($OldPackage) {
    if ($new.Version -eq $old.Version) {
        Add-Evidence $stepName "version did not change ($($new.Version)); this did not test an upgrade" 'FAIL'
    }

    # One family name across the upgrade is the whole upgrade-identity contract: a different one
    # means Windows installed a second application beside the first, and the user's data is
    # stranded in the old folder rather than lost — invisible, and worse than a crash.
    if ($new.PackageFamilyName -ne $old.PackageFamilyName) {
        Add-Evidence 'Upgrade identity' "family name changed: $($old.PackageFamilyName) -> $($new.PackageFamilyName). The upgrade installed side by side and orphaned the user's data." 'FAIL'
    } else {
        Add-Evidence 'Upgrade identity' "family name unchanged ($($new.PackageFamilyName))" 'PASS'
    }

    # Before first launch: the installer alone must not have rewritten anything.
    $fingerprintAfter = Get-DataFingerprint (Get-LocalStatePath)
    if (-not $fingerprintAfter) {
        Add-Evidence 'Data preserved' 'the database is gone after the upgrade' 'FAIL'
    }
    elseif ($fingerprintBefore -and $fingerprintAfter.Sha256 -eq $fingerprintBefore.Sha256) {
        Add-Evidence 'Data preserved' 'connectonion.db is byte-identical after the upgrade, before first launch' 'PASS'
    }
    elseif ($fingerprintBefore) {
        Add-Evidence 'Data preserved' "the database changed during install (was $($fingerprintBefore.Bytes) bytes, now $($fingerprintAfter.Bytes)) — the installer should not touch it at all" 'FAIL'
    }
}

# --- 3. the things only a person can see -------------------------------------------------------

Confirm-Manually 'First launch (new)' `
    'Launch the upgraded app. It starts without an error toast about the identity being reset.'

if ($OldPackage) {
    Confirm-Manually 'Identity preserved' `
        'Settings > Identity shows the SAME 0x address you wrote down, and the recovery phrase is still available if it was before.'
    Confirm-Manually 'Conversations preserved' `
        'The sidebar still lists the conversations and agents from the previous version, and opening one shows its history.'
}

Confirm-Manually 'Notification activation' `
    'Trigger a notification (a reply arriving while the window is not focused) and click the toast. The app comes forward on that conversation.'
Confirm-Manually 'Tray restore' `
    'Close the window with the title-bar X, choose Minimize to tray, then restore from the tray icon. The window comes back with its state intact.'

# --- 4. uninstall ------------------------------------------------------------------------------

if (-not $NonInteractive) {
    $answer = Read-Host "`n  Uninstall the package now to finish the matrix? [Y/n]"
    if ($answer -notmatch '^(n|no)$') {
        $package = Get-InstalledPackage
        Remove-AppxPackage -Package $package.PackageFullName
        if (Get-InstalledPackage) {
            Add-Evidence 'Uninstall' 'the package is still registered' 'FAIL'
        } else {
            Add-Evidence 'Uninstall' 'package removed' 'PASS'
        }
        Confirm-Manually 'Uninstall cleanliness' `
            'No ConnectOnion entry remains in Start menu or Settings > Apps, and no tray icon is left behind.'
    }
}

# --- report ------------------------------------------------------------------------------------

$stamp = (Get-Date).ToString('yyyy-MM-dd_HHmmss')
$logPath = Join-Path $OutDir "release-validation-$stamp.md"

$manualCount = @($evidence | Where-Object Result -eq 'MANUAL').Count

@(
    "# Release validation — $stamp",
    "",
    "| Field | Value |",
    "|---|---|",
    "| Machine | $env:COMPUTERNAME |",
    "| Windows | $((Get-CimInstance Win32_OperatingSystem).Version) |",
    "| Old package | $(if ($OldPackage) { Split-Path -Leaf $OldPackage } else { 'n/a (fresh install)' }) |",
    "| New package | $(Split-Path -Leaf $NewPackage) |",
    "",
    "| Time | Step | Result | Detail |",
    "|---|---|---|---|",
    ($evidence | ForEach-Object { "| $($_.Time) | $($_.Step) | $($_.Result) | $($_.Detail) |" })
    "",
    $(if ($failures.Count -gt 0) { "## Failures`n`n" + (($failures | ForEach-Object { "- $_" }) -join "`n") } else { "No failures recorded." }),
    $(if ($manualCount -gt 0) { "`n$manualCount checkpoint(s) were not verified; this run is not a complete validation." } else { "" })
) | Out-File -FilePath $logPath -Encoding utf8

Write-Host "`nEvidence: $logPath" -ForegroundColor Cyan

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) failure(s) — this release must not ship." -ForegroundColor Red
    exit 1
}
if ($manualCount -gt 0) {
    Write-Host "$manualCount checkpoint(s) unverified — incomplete validation, not a pass." -ForegroundColor Yellow
    exit 2
}

Write-Host "Validation complete." -ForegroundColor Green
exit 0
