<#
.SYNOPSIS
    Runs the real-window half of the trimming gate against a trimmed build.

.DESCRIPTION
    scripts/Invoke-TrimAudit.ps1 answers what the linker thinks (warning inventory, size) and
    proves the Core/Protocol round trip in a trimmed console binary. It cannot answer the two
    criteria in docs/TRIMMING.md that need the trimmed *app* to actually run:

      3. Tool Activity and every interactive card survive persist/restart in the trimmed publish
      4. Fake Agent and real-window smoke tests pass against the trimmed executable

    This script produces that evidence:

      1. Publishes the app trimmed WITH ReadyToRun, unless -TrimmedExe points at an existing one.
         R2R matters: the audit's trimmed publish sets PublishReadyToRun=false, so its size and
         startup are not comparable to the shipping untrimmed+R2R configuration. Trimmed+R2R is
         what would actually ship, so it is what gets tested.
      2. Seeds a data root by running the TRIMMED smoke harness's `persist` command. Both sides of
         the round trip are then trimmed binaries and nothing untrimmed touches the data.
      3. Runs the FlaUI shell suite against the trimmed executable      -> criterion 4.
      4. Runs TrimmedRuntimeTests against the seeded root               -> criterion 3.

    Needs a real interactive desktop session. Nothing here changes the build configuration;
    enabling trimming remains a deliberate edit to ConnectOnion.WinUIClient.csproj.

.PARAMETER TrimmedExe
    An existing trimmed ConnectOnion.WinUIClient.exe. Omit to publish one.

.PARAMETER SmokeExe
    An existing trimmed ConnectOnion.TrimSmoke.exe used to seed. Omit to publish one.

.PARAMETER OutDir
    Where publishes, the seeded root and the report are written.

.PARAMETER SkipShellSuite
    Skip criterion 4 (the 12-test shell suite) and run only the rendering check.

.EXAMPLE
    pwsh scripts/Test-TrimmedRuntime.ps1
.EXAMPLE
    pwsh scripts/Test-TrimmedRuntime.ps1 -TrimmedExe artifacts/trim-audit/trimmed-r2r/ConnectOnion.WinUIClient.exe
#>
[CmdletBinding()]
param(
    [string] $TrimmedExe,
    [string] $SmokeExe,
    [string] $OutDir,
    [switch] $SkipShellSuite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'artifacts/trim-runtime' }
$OutDir = if ([IO.Path]::IsPathRooted($OutDir)) {
    [IO.Path]::GetFullPath($OutDir)
} else {
    [IO.Path]::GetFullPath((Join-Path $RepoRoot $OutDir))
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$AppProject   = Join-Path $RepoRoot 'ConnectOnion.WinUIClient/ConnectOnion.WinUIClient.csproj'
$SmokeProject = Join-Path $RepoRoot 'tests/ConnectOnion.TrimSmoke/ConnectOnion.TrimSmoke.csproj'
$UiProject    = Join-Path $RepoRoot 'tests/ConnectOnion.WinUIClient.UITests/ConnectOnion.WinUIClient.UITests.csproj'

$report = [ordered]@{
    capturedUtc = (Get-Date).ToUniversalTime().ToString('o')
    machine     = $env:COMPUTERNAME
    sdk         = (& dotnet --version)
    commit      = (& git -C $RepoRoot rev-parse --short HEAD)
}

function Publish-Trimmed {
    param([string] $Project, [string] $Dir, [string[]] $ExtraArgs, [string] $LogPath)

    if (Test-Path $Dir) { Remove-Item -Recurse -Force $Dir }
    $publishArgs = @(
        'publish', $Project, '--configuration', 'Release', '--runtime', 'win-x64',
        '-p:AppxPackageSigningEnabled=false', '-p:PublishTrimmed=true', '-o', $Dir
    ) + $ExtraArgs

    Write-Host "  dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
    & dotnet @publishArgs *>&1 | Tee-Object -FilePath $LogPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $Project — see $LogPath" }
}

# --- 1. Trimmed + R2R app ----------------------------------------------------------------------

Write-Host "`n[1/4] Trimmed + ReadyToRun app publish" -ForegroundColor Cyan
if ($TrimmedExe) {
    $TrimmedExe = (Resolve-Path $TrimmedExe).Path
    Write-Host "  using existing: $TrimmedExe"
} else {
    $dir = Join-Path $OutDir 'app'
    Publish-Trimmed -Project $AppProject -Dir $dir -LogPath (Join-Path $OutDir 'app-publish.log') `
        -ExtraArgs @('-p:Platform=x64', '-p:RunUnpackaged=true', '-p:PublishReadyToRun=true')
    $TrimmedExe = Join-Path $dir 'ConnectOnion.WinUIClient.exe'
}
if (-not (Test-Path $TrimmedExe)) { throw "Trimmed app executable not found: $TrimmedExe" }
$notificationResource = Join-Path (Split-Path -Parent $TrimmedExe) `
    'Microsoft.WindowsAppRuntime.Insights.Resource.dll'
if (-not (Test-Path $notificationResource)) {
    throw "Trimmed app is missing the native Windows notification resource: $notificationResource"
}
$report.trimmedExe = $TrimmedExe
$report.notificationResource = $notificationResource

# --- 2. Seed a data root with the trimmed smoke harness -----------------------------------------

Write-Host "`n[2/4] Seeding a data root with the trimmed smoke harness" -ForegroundColor Cyan
if ($SmokeExe) {
    $SmokeExe = (Resolve-Path $SmokeExe).Path
    Write-Host "  using existing: $SmokeExe"
} else {
    $dir = Join-Path $OutDir 'smoke'
    Publish-Trimmed -Project $SmokeProject -Dir $dir `
        -LogPath (Join-Path $OutDir 'smoke-publish.log') `
        -ExtraArgs @('-p:NuGetLockFilePath=packages.trim-smoke.lock.json')
    $SmokeExe = Join-Path $dir 'ConnectOnion.TrimSmoke.exe'
}
if (-not (Test-Path $SmokeExe)) { throw "Trimmed smoke harness not found: $SmokeExe" }

$dataRoot = Join-Path $OutDir 'data-root'
if (Test-Path $dataRoot) { Remove-Item -Recurse -Force $dataRoot }
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null

$env:CONNECTONION_TRIMSMOKE_ROOT = $dataRoot
try {
    & $SmokeExe persist | Tee-Object -FilePath (Join-Path $OutDir 'seed.log') | Out-Null
    $seedExit = $LASTEXITCODE
}
finally {
    Remove-Item Env:\CONNECTONION_TRIMSMOKE_ROOT -ErrorAction SilentlyContinue
}
$report.seedPassed = ($seedExit -eq 0)
if ($seedExit -ne 0) { throw "Seeding failed (exit $seedExit) — see $(Join-Path $OutDir 'seed.log')" }
Write-Host "  seeded: $dataRoot" -ForegroundColor Green

# --- 3 & 4. Real-window suites against the trimmed executable ----------------------------------

$env:CONNECTONION_UI_TEST_EXE = $TrimmedExe
$env:CONNECTONION_UI_CAPTURE_DIR = Join-Path $OutDir 'screenshots'
New-Item -ItemType Directory -Force -Path $env:CONNECTONION_UI_CAPTURE_DIR | Out-Null

function Invoke-UiSuite {
    param([string] $Filter, [string] $TrxName, [string] $Root)

    $env:CONNECTONION_DATA_ROOT = $Root
    try {
        # dotnet writes failed-test diagnostics to stderr. With the script-wide Stop preference,
        # Windows PowerShell converts that native stderr into a terminating ErrorRecord and exits
        # before the TRX/report can be recorded. The process exit code is the authoritative suite
        # result, so allow its output through the pipeline and restore strict handling afterwards.
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & dotnet test $UiProject --configuration Release --filter $Filter `
                --results-directory (Join-Path $OutDir 'trx') --logger "trx;LogFileName=$TrxName" *>&1 |
                Tee-Object -FilePath (Join-Path $OutDir ($TrxName -replace '\.trx$', '.log')) |
                Out-Host
            $suiteExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        return ($suiteExitCode -eq 0)
    }
    finally {
        Remove-Item Env:\CONNECTONION_DATA_ROOT -ErrorAction SilentlyContinue
    }
}

try {
    Write-Host "`n[3/4] Criterion 3 — trimmed app renders the seeded turn" -ForegroundColor Cyan
    $report.renderingPassed = Invoke-UiSuite -Filter 'Category=TrimmedRuntime' `
        -TrxName 'trimmed-rendering.trx' -Root $dataRoot

    if ($SkipShellSuite) {
        Write-Host "`n[4/4] Criterion 4 — skipped (-SkipShellSuite)" -ForegroundColor Yellow
        $report.shellSuitePassed = $null
    } else {
        Write-Host "`n[4/4] Criterion 4 — shell smoke suite against the trimmed executable" -ForegroundColor Cyan
        # A separate, empty root: the shell suite seeds and asserts its own fixtures, and pointing
        # it at the seeded conversation would change what its first-run tests see.
        $shellRoot = Join-Path $OutDir 'shell-root'
        if (Test-Path $shellRoot) { Remove-Item -Recurse -Force $shellRoot }
        New-Item -ItemType Directory -Force -Path $shellRoot | Out-Null
        $report.shellSuitePassed = Invoke-UiSuite -Filter 'Category=UiSmoke' `
            -TrxName 'trimmed-shell.trx' -Root $shellRoot
    }
}
finally {
    Remove-Item Env:\CONNECTONION_UI_TEST_EXE -ErrorAction SilentlyContinue
    Remove-Item Env:\CONNECTONION_UI_CAPTURE_DIR -ErrorAction SilentlyContinue
}

# --- Report -------------------------------------------------------------------------------------

$reportPath = Join-Path $OutDir 'trim-runtime.json'
($report | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Host "`n---- Trimming gate: real-window criteria ----" -ForegroundColor Cyan
Write-Host ("  criterion 3 (renders seeded turn) : {0}" -f $(if ($report.renderingPassed) { 'PASS' } else { 'FAIL' })) `
    -ForegroundColor $(if ($report.renderingPassed) { 'Green' } else { 'Red' })
if ($null -eq $report.shellSuitePassed) {
    Write-Host "  criterion 4 (shell smoke suite)   : SKIPPED" -ForegroundColor Yellow
} else {
    Write-Host ("  criterion 4 (shell smoke suite)   : {0}" -f $(if ($report.shellSuitePassed) { 'PASS' } else { 'FAIL' })) `
        -ForegroundColor $(if ($report.shellSuitePassed) { 'Green' } else { 'Red' })
}
Write-Host "`nReport: $reportPath"

$failed = (-not $report.renderingPassed) -or ($report.shellSuitePassed -eq $false)
if ($failed) { exit 1 }
