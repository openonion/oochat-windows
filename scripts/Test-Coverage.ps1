<#
.SYNOPSIS
    Merged production line-coverage gate, ratcheted against a recorded baseline.

.DESCRIPTION
    This gate used to be two absolute thresholds. They were set so close to the measured values
    (Protocol had 3 lines of slack, Core had 6) that any ordinary change which added a branch
    nobody covered broke CI — which pushes a contributor toward one of two bad answers: writing a
    padding test that asserts nothing, or quietly lowering the number. Neither is what a coverage
    gate is for.

    So the gate is a ratchet. `coverage-baseline.json` records the high-water mark per assembly.
    A run fails only when coverage falls **below that baseline** by more than -Tolerance, which is
    what "don't regress" actually means. When coverage rises the run passes and prints the new
    figure; -UpdateBaseline writes it back so the ratchet advances deliberately rather than on
    every incidental fluctuation.

    -Tolerance exists because the number is not perfectly reproducible: a few lines in timing- and
    environment-dependent paths flip between runs. Without it the ratchet fails on noise, which
    teaches people to ignore it.

    The -*Floor parameters are a separate, absolute safety net. The baseline can advance on its
    own, but it can never sink past a floor without someone editing this file — so a slow drift
    downward through repeated -UpdateBaseline runs still has to cross a line that shows up in
    review.

.NOTES
    Core's baseline is materially lower than the 88.5% this gate used to demand, and that is a
    change in what is measured, not a drop in what is tested. The run runtime (AgentSessionManager
    and friends, ~1,300 lines) lived in the app project, which no headless test host can load, so
    it was outside the measured set entirely. Moving it into Core brought it under test — and put
    its still-uncovered socket half into the denominator, where it is now visible. The honest
    number for the same code is the lower one.
#>
[CmdletBinding()]
param(
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '..\TestResults'),

    # Resolved in the body, not here: Windows PowerShell 5.1 does not populate $PSScriptRoot while
    # binding param defaults under -File, so a Join-Path default throws on an empty string.
    [string]$BaselinePath = '',

    # Percentage points a run may fall below the baseline before it fails. Absorbs run-to-run
    # noise; large enough to stop false alarms, small enough that a real regression still trips.
    [ValidateRange(0, 5)]
    [double]$Tolerance = 0.25,

    # Rewrites the baseline to the measured values. Run deliberately, never in CI.
    [switch]$UpdateBaseline,

    # Absolute minimums. Independent of the baseline and never written by -UpdateBaseline.
    [ValidateRange(0, 100)]
    [double]$ProtocolLineFloor = 85.0,
    [ValidateRange(0, 100)]
    [double]$CoreLineFloor = 82.0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $BaselinePath = Join-Path $scriptRoot '..\coverage-baseline.json'
}

function Get-CoberturaReports {
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string[]]$Suites
    )

    $reports = foreach ($suite in $Suites) {
        $suiteRoot = Join-Path $Root $suite
        if (-not (Test-Path -LiteralPath $suiteRoot -PathType Container)) {
            throw "Coverage results are missing for the '$suite' test suite: $suiteRoot"
        }

        Get-ChildItem -LiteralPath $suiteRoot -Recurse -Filter 'coverage.cobertura.xml' -File
    }

    if (@($reports).Count -eq 0) {
        throw "No coverage.cobertura.xml files were found under $Root"
    }

    return @($reports)
}

function Get-MergedLineCoverage {
    param(
        [Parameter(Mandatory)]
        [string]$Assembly,
        [Parameter(Mandatory)]
        [System.IO.FileInfo[]]$Reports
    )

    # The same Core source is instrumented by both the unit and integration suites. Merge by
    # source-file + line and keep the highest hit count so executable lines are counted once.
    $lines = @{}
    foreach ($report in $Reports) {
        [xml]$coverage = Get-Content -LiteralPath $report.FullName -Raw
        foreach ($package in @($coverage.coverage.packages.package)) {
            if ([string]$package.name -ne $Assembly) { continue }

            foreach ($class in @($package.classes.class)) {
                $file = ([string]$class.filename).Replace('/', '\')
                if ($file -match '(^|\\)obj\\') { continue }

                # Coverlet can emit an empty <lines /> element for compiler-generated async
                # state-machine classes that contain no executable sequence points. Under
                # StrictMode, accessing the missing `.line` property throws before the ratchet
                # can evaluate any coverage. SelectNodes returns an empty collection instead.
                foreach ($line in @($class.SelectNodes('lines/line'))) {
                    $key = "$file|$($line.number)"
                    $covered = [int]$line.hits -gt 0
                    if (-not $lines.ContainsKey($key) -or $covered) {
                        $lines[$key] = $covered
                    }
                }
            }
        }
    }

    if ($lines.Count -eq 0) {
        throw "Coverage reports did not contain executable lines for $Assembly."
    }

    $coveredLines = @($lines.Values | Where-Object { $_ }).Count
    [pscustomobject]@{
        Assembly = $Assembly
        Covered  = $coveredLines
        Total    = $lines.Count
        Rate     = 100.0 * $coveredLines / $lines.Count
    }
}

function Get-UnmeasuredSurface {
    <#
        Counts the production code that no coverage number in this report covers.

        The app project cannot be instrumented. A headless test host cannot load it (it drags in
        the Windows App SDK), and the FlaUI suite launches it as a separate process, which
        coverlet's in-process collector does not follow. So its assembly never appears in any
        Cobertura report — not as 0%, but as nothing at all.

        Absent and zero look identical in a report that only lists what it measured, and this is
        the larger half of the codebase. Printing the size of the blind spot is not a coverage
        measurement and must not be read as one; it is there so the two gated numbers stop
        implying they describe the whole product.

        Physical source lines, so this is deliberately NOT comparable to the covered/total line
        counts above (those count sequence points). It answers "how much is out of scope", not
        "how much is untested".
    #>
    param([Parameter(Mandatory)][string]$RepoRoot)

    $appRoot = Join-Path $RepoRoot 'ConnectOnion.WinUIClient'
    if (-not (Test-Path -LiteralPath $appRoot)) { return $null }

    $csLines = 0; $csFiles = 0
    $xamlLines = 0; $xamlFiles = 0

    # Filtered on .Extension, NOT with -Include. In Windows PowerShell 5.1, -Include combined with
    # -LiteralPath and -Recurse does not reliably restrict to the pattern: asking for '*.xaml' here
    # returned 230 files including the 100 MB .msix packages under AppPackages\, which summed to
    # 1.8 million "lines" of XAML against a true figure of 9,073. A number that wrong in a report
    # nobody re-derives is worse than no number, so the enumeration is explicit.
    #
    # AppPackages is excluded alongside bin/obj: it is MSIX staging output, not source, and it is
    # not always cleaned between builds.
    foreach ($file in Get-ChildItem -LiteralPath $appRoot -Recurse -File -ErrorAction SilentlyContinue) {
        if ($file.FullName -match '\\(bin|obj|AppPackages)\\') { continue }
        if ($file.Extension -notin '.cs', '.xaml') { continue }

        $count = @(Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue).Count
        if ($file.Extension -eq '.cs') { $csLines += $count; $csFiles++ }
        else { $xamlLines += $count; $xamlFiles++ }
    }

    [pscustomobject]@{
        CsFiles = $csFiles; CsLines = $csLines
        XamlFiles = $xamlFiles; XamlLines = $xamlLines
    }
}

function Get-Baseline {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][hashtable]$Floors
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        # A missing baseline seeds from the floors rather than from the current run: seeding from
        # the run would rubber-stamp whatever coverage happens to be present, including a drop.
        Write-Host "No baseline at $Path - seeding from the configured floors."
        return $Floors.Clone()
    }

    $parsed = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $baseline = @{}
    foreach ($assembly in $Floors.Keys) {
        $baseline[$assembly] = if ($parsed.PSObject.Properties.Name -contains $assembly) {
            [double]$parsed.$assembly
        } else {
            $Floors[$assembly]
        }
    }
    return $baseline
}

$resolvedResults = (Resolve-Path -LiteralPath $ResultsDirectory).Path
$protocolReports = Get-CoberturaReports -Root $resolvedResults -Suites @('protocol')
$coreReports = Get-CoberturaReports -Root $resolvedResults -Suites @('client', 'integration')

$floors = @{
    'ConnectOnion.Protocol'         = $ProtocolLineFloor
    'ConnectOnion.WinUIClient.Core' = $CoreLineFloor
}
$baseline = Get-Baseline -Path $BaselinePath -Floors $floors

$measured = @(
    Get-MergedLineCoverage -Assembly 'ConnectOnion.Protocol' -Reports $protocolReports
    Get-MergedLineCoverage -Assembly 'ConnectOnion.WinUIClient.Core' -Reports $coreReports
)

$results = foreach ($item in $measured) {
    $assemblyBaseline = $baseline[$item.Assembly]
    $floor = $floors[$item.Assembly]
    $required = [Math]::Max($assemblyBaseline - $Tolerance, $floor)

    $verdict =
        if ($item.Rate -lt $floor) { 'FAIL (floor)' }
        elseif ($item.Rate -lt $assemblyBaseline - $Tolerance) { 'FAIL (regressed)' }
        elseif ($item.Rate -gt $assemblyBaseline + $Tolerance) { 'PASS (improved)' }
        else { 'PASS' }

    [pscustomobject]@{
        Assembly = $item.Assembly
        Covered  = $item.Covered
        Total    = $item.Total
        Rate     = $item.Rate
        Baseline = $assemblyBaseline
        Required = $required
        Verdict  = $verdict
        Passed   = -not $verdict.StartsWith('FAIL')
        Improved = $verdict -eq 'PASS (improved)'
    }
}
$results = @($results)

Write-Host 'Merged production line coverage (duplicate source lines counted once):'
$results |
    Select-Object Assembly, Covered, Total,
        @{ Name = 'Rate';     Expression = { '{0:N2}%' -f $_.Rate } },
        @{ Name = 'Baseline'; Expression = { '{0:N2}%' -f $_.Baseline } },
        @{ Name = 'Required'; Expression = { '{0:N2}%' -f $_.Required } },
        @{ Name = 'Result';   Expression = { $_.Verdict } } |
    Format-Table -AutoSize

$repoRoot = Split-Path -Parent (Split-Path -Parent $BaselinePath)
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'ConnectOnion.WinUIClient'))) {
    $repoRoot = Split-Path -Parent $BaselinePath
}
$unmeasured = Get-UnmeasuredSurface -RepoRoot $repoRoot

if ($unmeasured) {
    Write-Host 'Not covered by any number above (cannot be instrumented, see script header):'
    Write-Host ("  ConnectOnion.WinUIClient  {0,6} source lines across {1} .cs files, plus {2} lines of XAML in {3} files" -f `
        $unmeasured.CsLines, $unmeasured.CsFiles, $unmeasured.XamlLines, $unmeasured.XamlFiles)
    Write-Host '  Its only cover is the FlaUI shell suite, which runs the app out-of-process.'
    Write-Host ''
}

if ($env:GITHUB_STEP_SUMMARY) {
    $summary = @(
        '## Coverage gate'
        ''
        "Ratcheted against ``coverage-baseline.json`` with a $($Tolerance.ToString('N2'))pp tolerance."
        ''
        '| Assembly | Covered lines | Total lines | Coverage | Baseline | Required | Result |'
        '| --- | ---: | ---: | ---: | ---: | ---: | --- |'
    )
    foreach ($result in $results) {
        $summary += "| $($result.Assembly) | $($result.Covered) | $($result.Total) | $($result.Rate.ToString('N2'))% | $($result.Baseline.ToString('N2'))% | $($result.Required.ToString('N2'))% | $($result.Verdict) |"
    }
    if ($unmeasured) {
        $summary += ''
        $summary += '### Outside the measured set'
        $summary += ''
        $summary += "``ConnectOnion.WinUIClient`` contributes **$($unmeasured.CsLines) source lines** across $($unmeasured.CsFiles) `.cs` files, plus $($unmeasured.XamlLines) lines of XAML in $($unmeasured.XamlFiles) files, and appears in **no** coverage figure above."
        $summary += ''
        $summary += 'It cannot be instrumented: a headless test host cannot load the Windows App SDK, and the FlaUI shell suite runs the app as a separate process. Absent and 0% look the same in a report that lists only what it measured, so this is stated rather than left blank. Source lines are not comparable to the sequence-point counts in the table — this says how much is out of scope, not how much is untested.'
    }
    $summary += ''
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary
}

if ($UpdateBaseline) {
    $updated = [ordered]@{}
    foreach ($result in ($results | Sort-Object Assembly)) {
        # Never ratchet below a floor, even on an explicit update.
        $updated[$result.Assembly] = [Math]::Round([Math]::Max($result.Rate, $floors[$result.Assembly]), 2)
    }
    ($updated | ConvertTo-Json) | Set-Content -LiteralPath $BaselinePath -Encoding utf8
    Write-Host "Baseline written to $BaselinePath."
}
elseif (@($results | Where-Object Improved).Count -gt 0) {
    Write-Host ''
    Write-Host 'Coverage improved above the baseline. Lock it in with:' -ForegroundColor Green
    Write-Host '  pwsh scripts/Test-Coverage.ps1 -ResultsDirectory <dir> -UpdateBaseline' -ForegroundColor Green
}

$failures = @($results | Where-Object { -not $_.Passed })
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Error -ErrorAction Continue (
            '{0} line coverage is {1:N2}%, below the required {2:N2}% (baseline {3:N2}%, tolerance {4:N2}pp).' -f `
                $failure.Assembly, $failure.Rate, $failure.Required, $failure.Baseline, $Tolerance)
    }
    exit 1
}

Write-Host 'Coverage gate passed.'
