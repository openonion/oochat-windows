<#
.SYNOPSIS
    Audits a built release archive: what is inside it, and how big it got.

.DESCRIPTION
    Two gates that both have to hold before a package may be published.

    CONTENTS. The release payload is produced by the same build that produced the test projects,
    and it is assembled from a publish directory rather than an explicit allow-list — so the way
    a test assembly or a PDB reaches a user is not a decision anyone makes, it is one nobody
    notices. The audit makes a clean payload a property of every build rather than an observation
    about one.

    SIZE. Compressed size is what a user downloads and unpacked size is roughly what the install
    costs on disk, so both are reported against the ratified baseline and both must stay inside
    the budget. The gate exists to catch an accidental payload — a second copy of the runtime, an
    unpruned resource set — not to police a few megabytes of real features, so the budget is
    deliberately loose and a breach is a prompt to look, not an automatic no.

.PARAMETER PackagePath
    The .msix / .msixbundle / portable .zip to audit.

.PARAMETER BaselineCompressedMb
    Override the ratified baseline. Default: 121.43 MB for self-contained MSIX; 71.29 MB for ZIP.

.PARAMETER BaselineUnpackedMb
    Override the ratified baseline. Default: 310.07 MB for self-contained MSIX; 170.71 MB for ZIP.

.PARAMETER BudgetPercent
    Allowed growth over baseline before the gate fails. Default 10, matching the performance
    harness's regression convention.

.PARAMETER SkipSizeGate
    Report sizes without failing on them. For a build that knowingly adds payload and needs to
    re-ratify the baseline.

.EXAMPLE
    pwsh scripts/Test-PackageContents.ps1 -PackagePath artifacts/release/ConnectOnion.Desktop.msix
.EXAMPLE
    pwsh scripts/Test-PackageContents.ps1 -PackagePath artifacts/release/ConnectOnion.Desktop-portable.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [double] $BaselineCompressedMb = 0,
    [double] $BaselineUnpackedMb   = 0,
    [double] $BudgetPercent        = 10,
    [switch] $SkipSizeGate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PackagePath)) { throw "Package not found at $PackagePath." }
$PackagePath = (Resolve-Path $PackagePath).Path
$isMsix = [IO.Path]::GetExtension($PackagePath) -in '.msix', '.appx'

# Both forms carry .NET and Windows App SDK. Packaging overhead differs, so each release form has
# its own ratified baseline. Explicit parameters still override these defaults.
#
# The portable baseline was re-ratified on 2026-08-05 when trimming was enabled for Release
# (docs/TRIMMING.md): 118.91 -> 71.29 MB compressed, 309.21 -> 170.71 MB unpacked. Re-ratifying is
# not bookkeeping — this gate is an upper bound, so leaving the untrimmed numbers in place would
# have left roughly 60 MB of headroom below the limit, and the accidental second copy of the
# runtime this exists to catch would have sailed through it.
#
# The MSIX numbers are NOT re-ratified: no MSIX is built (see docs/RELEASE.md), so they still
# describe an untrimmed package. Re-measure them against a real trimmed package if that path is
# ever restored, rather than scaling these by eye.
if (-not $PSBoundParameters.ContainsKey('BaselineCompressedMb')) {
    $BaselineCompressedMb = if ($isMsix) { 121.43 } else { 71.29 }
}
if (-not $PSBoundParameters.ContainsKey('BaselineUnpackedMb')) {
    $BaselineUnpackedMb = if ($isMsix) { 310.07 } else { 170.71 }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

<#
    Each rule is a name and a predicate over the entry path. Written as "what must not be here and
    why" rather than a bare glob list, because the reason is the part that decides whether a future
    match is a real finding or a rule that needs narrowing.
#>
# `(^|[/\\])` throughout, not `(^|/)`: OPC entry names use forward slashes, but a package
# assembled by other tooling can carry backslashes, and a rule that silently stops matching is
# worse than no rule.
$forbidden = @(
    @{ Name = 'Debug symbols (publish separately)'; Match = { param($p) $p -like '*.pdb' } },
    @{ Name = 'xunit / VSTest assemblies';          Match = { param($p) $p -match '(?i)(^|[/\\])(xunit|testhost|Microsoft\.TestPlatform|Microsoft\.VisualStudio\.TestPlatform|coverlet)' } },
    @{ Name = 'Test projects';                      Match = { param($p) $p -match '(?i)(^|[/\\])ConnectOnion\.(.*\.)?(Tests|UnitTests|UITests|IntegrationTests|TrimSmoke|Conformance|LiveTest)\.' } },
    @{ Name = 'UI-automation / architecture test dependencies'; Match = { param($p) $p -match '(?i)(^|[/\\])(FlaUI|Interop\.UIAutomationClient|ArchUnitNET|Mono\.Cecil)' } },
    @{ Name = 'Log files';                          Match = { param($p) $p -like '*.log' } },
    @{ Name = 'Development databases';              Match = { param($p) $p -like '*.db' -or $p -like '*.db-wal' -or $p -like '*.db-shm' } },
    @{ Name = 'Coverage output';                    Match = { param($p) $p -like '*.cobertura.xml' -or $p -match '(?i)(^|[/\\])TestResults[/\\]' } },
    @{ Name = 'Source archives / node reference signer'; Match = { param($p) $p -like '*.cs' -or $p -like '*ref-sign.js' -or $p -match '(?i)(^|[/\\])node_modules[/\\]' } }
)

Write-Host "Auditing $PackagePath`n"

$zip = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
try {
    $entries = @($zip.Entries | Where-Object { $_.FullName -notmatch '/$' })

    $unpackedBytes = ($entries | Measure-Object -Property Length -Sum).Sum
    $entryCount    = $entries.Count

    $violations = @()
    if (-not $isMsix) {
        $rootFiles = @($entries | Where-Object { $_.FullName -notmatch '[/\\]' })
        if ($rootFiles.Count -ne 1 -or
            $rootFiles[0].FullName -ne 'ConnectOnion.WinUIClient.exe') {
            $violations += [pscustomobject]@{
                Rule = 'Portable root contains only the launcher'
                Files = @($rootFiles | Select-Object -ExpandProperty FullName)
            }
        }

        $requiredNestedPayload = @(
            'app/ConnectOnion.WinUIClient.exe',
            'app/coreclr.dll',
            'app/Microsoft.WindowsAppRuntime.dll',
            # Windows App SDK 2.3.1 leaves this in its framework MSIX instead of the
            # self-contained component folders. AppNotificationManager.Register loads it by
            # name, so its absence disables every Windows notification with 0x8007007E.
            'app/Microsoft.WindowsAppRuntime.Insights.Resource.dll'
        )
        $entryNames = @($entries | ForEach-Object { $_.FullName -replace '\\', '/' })
        $missingNestedPayload = @($requiredNestedPayload | Where-Object { $_ -notin $entryNames })
        if ($missingNestedPayload.Count -gt 0) {
            $violations += [pscustomobject]@{
                Rule = 'Nested self-contained application payload'
                Files = $missingNestedPayload
            }
        }

        $rootDlls = @($rootFiles | Where-Object { $_.FullName -like '*.dll' })
        if ($rootDlls.Count -gt 0) {
            $violations += [pscustomobject]@{
                Rule = 'DLLs must stay under app/'
                Files = @($rootDlls | Select-Object -ExpandProperty FullName)
            }
        }
    }

    foreach ($rule in $forbidden) {
        $hits = @($entries | Where-Object { & $rule.Match $_.FullName } | Select-Object -ExpandProperty FullName)
        if ($hits.Count -gt 0) {
            $violations += [pscustomobject]@{ Rule = $rule.Name; Files = $hits }
        }
    }

    # A release asset is promised as a one-file installer. Any PackageDependency in its
    # manifest makes that promise false on a clean sideload machine unless the matching
    # framework package is distributed and installed separately. The release build is required
    # to be Windows App SDK self-contained, so fail closed on every external package dependency.
    if ($isMsix) {
        $manifestEntry = $zip.GetEntry('AppxManifest.xml')
        if (-not $manifestEntry) {
            $violations += [pscustomobject]@{
                Rule = 'MSIX manifest'
                Files = @('AppxManifest.xml is missing')
            }
        }
        else {
            $stream = $manifestEntry.Open()
            $reader = [IO.StreamReader]::new($stream)
            try {
                [xml] $manifest = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
                $stream.Dispose()
            }

            $dependencies = @($manifest.SelectNodes(
                "/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']"))
            if ($dependencies.Count -gt 0) {
                $names = @($dependencies | ForEach-Object {
                    $name = $_.GetAttribute('Name')
                    $minimum = $_.GetAttribute('MinVersion')
                    "$name (minimum $minimum)"
                })
                $violations += [pscustomobject]@{
                    Rule = 'External MSIX framework dependency'
                    Files = $names
                }
            }
        }
    }
}
finally {
    $zip.Dispose()
}

$compressedBytes = (Get-Item $PackagePath).Length
$compressedMb    = [math]::Round($compressedBytes / 1MB, 2)
$unpackedMb      = [math]::Round($unpackedBytes / 1MB, 2)

# --- contents ---------------------------------------------------------------------------------

Write-Host "Contents"
Write-Host "  entries: $entryCount"
if ($violations.Count -eq 0) {
    Write-Host "  no forbidden content" -ForegroundColor Green
}
else {
    foreach ($violation in $violations) {
        Write-Host "  FORBIDDEN: $($violation.Rule)" -ForegroundColor Red
        # Capped: one stray reference can drag in a hundred files, and the first few name it.
        foreach ($file in ($violation.Files | Select-Object -First 10)) {
            Write-Host "    $file" -ForegroundColor Red
        }
        if ($violation.Files.Count -gt 10) {
            Write-Host "    ... and $($violation.Files.Count - 10) more" -ForegroundColor Red
        }
    }
}

# --- size -------------------------------------------------------------------------------------

$compressedLimit = [math]::Round($BaselineCompressedMb * (1 + $BudgetPercent / 100), 2)
$unpackedLimit   = [math]::Round($BaselineUnpackedMb   * (1 + $BudgetPercent / 100), 2)

function Format-Delta {
    param([double] $Actual, [double] $Baseline)
    if ($Baseline -le 0) { return 'n/a' }
    $percent = [math]::Round(100 * ($Actual - $Baseline) / $Baseline, 1)
    $sign = if ($percent -ge 0) { '+' } else { '' }
    return "$sign$percent%"
}

Write-Host "`nSize (ratified x64 $(if ($isMsix) { 'self-contained MSIX' } else { 'portable ZIP' }), budget +$BudgetPercent%)"
Write-Host ("  compressed : {0,7:N2} MB   baseline {1,7:N2} MB   {2,7}   limit {3,7:N2} MB" -f `
    $compressedMb, $BaselineCompressedMb, (Format-Delta $compressedMb $BaselineCompressedMb), $compressedLimit)
Write-Host ("  unpacked   : {0,7:N2} MB   baseline {1,7:N2} MB   {2,7}   limit {3,7:N2} MB" -f `
    $unpackedMb, $BaselineUnpackedMb, (Format-Delta $unpackedMb $BaselineUnpackedMb), $unpackedLimit)

$sizeBreaches = @()
if ($compressedMb -gt $compressedLimit) { $sizeBreaches += "compressed $compressedMb MB > $compressedLimit MB" }
if ($unpackedMb   -gt $unpackedLimit)   { $sizeBreaches += "unpacked $unpackedMb MB > $unpackedLimit MB" }

# --- machine-readable output -------------------------------------------------------------------

if ($env:GITHUB_OUTPUT) {
    "compressed_mb=$compressedMb" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "unpacked_mb=$unpackedMb"     | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "entry_count=$entryCount"     | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}

if ($env:GITHUB_STEP_SUMMARY) {
    $baselineKind = if ($isMsix) { 'self-contained MSIX' } else { 'portable ZIP' }
    $baselineEntries = if ($isMsix) { 632 } else { 466 }
    @(
        "### Package audit",
        "",
        "| Metric | Value | Baseline ($baselineKind) | Delta | Limit (+$BudgetPercent%) |",
        "|---|---|---|---|---|",
        "| Compressed | $compressedMb MB | $BaselineCompressedMb MB | $(Format-Delta $compressedMb $BaselineCompressedMb) | $compressedLimit MB |",
        "| Unpacked | $unpackedMb MB | $BaselineUnpackedMb MB | $(Format-Delta $unpackedMb $BaselineUnpackedMb) | $unpackedLimit MB |",
        "| Entries | $entryCount | $baselineEntries | | |",
        "",
        $(if ($violations.Count -eq 0) { "No forbidden content." }
          else { "**Forbidden content:** " + (($violations | ForEach-Object { $_.Rule }) -join '; ') })
    ) | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}

# --- verdict ------------------------------------------------------------------------------------

$failed = $false

if ($violations.Count -gt 0) {
    Write-Host "`nFAIL: the package contains content that must never ship." -ForegroundColor Red
    $failed = $true
}

if ($sizeBreaches.Count -gt 0) {
    if ($SkipSizeGate) {
        Write-Host "`nSize budget exceeded ($($sizeBreaches -join '; ')) — not failing, -SkipSizeGate was passed." -ForegroundColor Yellow
    }
    else {
        Write-Host "`nFAIL: size regression — $($sizeBreaches -join '; ')." -ForegroundColor Red
        Write-Host "Find the payload, or re-ratify the baseline in docs/RELEASE.md deliberately." -ForegroundColor Red
        Write-Host "Trimming is already on, so it is not a lever to get back under budget — a breach here" -ForegroundColor Red
        Write-Host "means real new payload. See docs/TRIMMING.md before touching that setting." -ForegroundColor Red
        $failed = $true
    }
}

if ($failed) { exit 1 }

Write-Host "`nPackage audit passed." -ForegroundColor Green
exit 0
