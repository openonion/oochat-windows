<#
.SYNOPSIS
    Captures an administrator-grade WPR trace around the startup benchmark.

.DESCRIPTION
    GeneralProfile supplies process lifetime, sampled CPU, disk/file I/O and system activity.
    XAMLActivity adds XAML layout/rendering events when that built-in profile is available.
    CLR GC/allocation events are included by the .NET runtime providers enabled by the general
    profile. SQLite activity is inspected through the app process's file-I/O stack to
    winsqlite3.dll and the selected dataset's connectonion.db.

    This script intentionally fails when it is not elevated, WPR is unavailable, a requested
    profile is blocked by policy, or the benchmark fails. It never emits an empty ETL as success.
#>
[CmdletBinding()]
param(
    [ValidateSet('WarmUnpackaged', 'ColdUnpackaged', 'WarmMsix', 'ColdMsix')]
    [string] $Mode = 'WarmUnpackaged',
    [string] $OutDir,
    [string] $Exe,
    [string] $DatasetId = 'empty-isolated-profile',
    [string] $FixturePath,
    [int]    $Iterations = 5,
    [string[]] $Profiles = @(
        'GeneralProfile',
        'DotNET',
        'XAMLActivity',
        'XAMLAppResponsiveness',
        'DesktopComposition'
    ),
    [switch] $RequireConversation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'WPR capture requires an elevated PowerShell session. No trace was started.'
}

$wpr = Get-Command wpr.exe -ErrorAction SilentlyContinue
if (-not $wpr) {
    throw 'wpr.exe was not found. Install the Windows Performance Toolkit from the Windows ADK.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) {
    $OutDir = Join-Path $repoRoot ("TestResults\perf-trace\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$null = New-Item -ItemType Directory -Force -Path $OutDir
$tracePath = Join-Path $OutDir 'startup.etl'

$startArgs = @()
foreach ($profile in $Profiles) {
    $startArgs += @('-start', $profile)
}
$startArgs += '-filemode'

& $wpr.Source @startArgs
if ($LASTEXITCODE -ne 0) {
    throw "WPR could not start profiles [$($Profiles -join ', ')] (exit $LASTEXITCODE). Check administrator rights and machine policy; policy error 0xc5585011 is not a valid trace."
}

$stopped = $false
try {
    $benchmarkArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $PSScriptRoot 'Measure-Performance.ps1'),
        '-Mode', $Mode,
        '-Iterations', "$Iterations",
        '-UnderInstrumentation',
        '-OutDir', $OutDir,
        '-DatasetId', $DatasetId
    )
    if ($Exe) { $benchmarkArgs += @('-Exe', $Exe) }
    if ($FixturePath) { $benchmarkArgs += @('-FixturePath', $FixturePath) }
    if ($RequireConversation) { $benchmarkArgs += '-RequireConversation' }

    & powershell.exe @benchmarkArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Startup benchmark failed with exit code $LASTEXITCODE; cancelling trace."
    }

    & $wpr.Source -stop $tracePath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $tracePath)) {
        throw "WPR failed to save '$tracePath' (exit $LASTEXITCODE)."
    }
    $stopped = $true
}
finally {
    if (-not $stopped) {
        & $wpr.Source -cancel 2>$null
    }
}

Write-Host "Trace  : $tracePath"
Write-Host "Report : $(Join-Path $OutDir 'report.md')"
Write-Host "Raw    : $(Join-Path $OutDir 'results.json')"
