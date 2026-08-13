<#
.SYNOPSIS
    Launch-time and memory benchmark for the ConnectOnion desktop client.

.DESCRIPTION
    Repeatedly launches the app, reads the startup report it writes (see
    Diagnostics/StartupTelemetry.cs), samples process memory once the app has settled at idle,
    then closes it and times the shutdown. Results are aggregated (median / min / max / p95),
    compared against the budgets below, and written out as JSON + Markdown.

    Metrics follow the Windows app performance guidance: time to first frame for launch,
    working set and private bytes for memory, measured on a Release build of the real shipping
    binary. A Debug build is refused unless -AllowDebugBuild is passed, because JIT-tiering and
    disabled optimizations make its numbers meaningless as a budget.

    Each iteration runs against a throwaway data root (CONNECTONION_DATA_ROOT), so a large
    local conversation history can't skew the result and the benchmark can't touch real data.
    Pass -UseRealDataRoot to measure launch against your actual profile instead.

.PARAMETER Exe
    Path to ConnectOnion.WinUIClient.exe. Defaults to the Release x64 unpackaged build.

.PARAMETER Iterations
    Measured launches for the selected mode. Default and minimum 5.

.PARAMETER Mode
    One of WarmUnpackaged, ColdUnpackaged, WarmMsix, or ColdMsix. Cold modes require successful
    standby-list purge before every sample; MSIX modes resolve the installed package executable.

.PARAMETER SettleSeconds
    Idle time after the readiness milestones before memory is sampled. Reaching the milestones
    proves the shell is ready, not that startup work has drained — presence probes and lazy
    first-use allocations are still running — so sampling on the milestone alone understates
    steady state and is not comparable to the ratified baseline. Default 15.

.PARAMETER UnderInstrumentation
    Set by Capture-StartupTrace.ps1 when this run happens inside a WPR trace. Budget verdicts are
    withheld and the report says why: ETW adds ~20% to launch and ~70% to shutdown here, so a
    traced run answers "where does the time go", never "does this build pass".

.PARAMETER EnforceBudgets
    Exit non-zero when a median metric breaches its Fail threshold. Off by default so the first
    run can establish the baseline rather than fail against a guess.

.EXAMPLE
    pwsh scripts/Measure-Performance.ps1
.EXAMPLE
    pwsh scripts/Measure-Performance.ps1 -Mode ColdUnpackaged -Iterations 5 -EnforceBudgets
#>
[CmdletBinding()]
param(
    [string] $Exe,
    [ValidateSet('WarmUnpackaged', 'ColdUnpackaged', 'WarmMsix', 'ColdMsix')]
    [string] $Mode = 'WarmUnpackaged',
    [int]    $Iterations = 5,
    [int]    $SettleSeconds = 15,
    [int]    $LaunchTimeoutSeconds = 60,
    [int]    $ReadinessTimeoutSeconds = 60,
    [string] $OutDir,
    [string] $PackageName = 'ConnectOnion.Desktop',
    [string] $PackageExecutable = 'ConnectOnion.WinUIClient.exe',
    [string] $DatasetId = 'empty-isolated-profile',
    [string] $FixturePath,
    [switch] $RequireConversation,
    [switch] $UnderInstrumentation,
    [switch] $UseRealDataRoot,
    [switch] $AllowDebugBuild,
    [switch] $EnforceBudgets
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProcessName = 'ConnectOnion.WinUIClient'

if ($Iterations -lt 5) {
    throw "At least five valid samples are required; -Iterations must be 5 or greater."
}

# Refused rather than silently ignored: a traced run withholds every verdict, so -EnforceBudgets
# would pass unconditionally and report a gate that never ran.
if ($UnderInstrumentation -and $EnforceBudgets) {
    throw '-EnforceBudgets cannot be combined with -UnderInstrumentation: a traced run has no budget verdict to enforce.'
}

# Budgets, in the units each metric is reported in. "Target" is what we want to hold; "Fail" is
# the line that makes a build unacceptable. Launch numbers are anchored on the Store
# certification requirement that an app must be responsive within 5 s, with a target well under
# it; memory budgets are the current shipped behaviour rounded up, and should be re-ratified
# whenever a run establishes a new baseline (see docs/PERFORMANCE.md).
$Budgets = @(
    [pscustomobject]@{ Metric = 'ColdStartMs';      Label = 'Cold start to first frame'; Target = 2000; Fail = 5000; Unit = 'ms' }
    [pscustomobject]@{ Metric = 'WarmStartMs';      Label = 'Warm start to first frame'; Target = 1200; Fail = 2500; Unit = 'ms' }
    [pscustomobject]@{ Metric = 'IdleWorkingSetMb'; Label = 'Idle working set';          Target = 200;  Fail = 350;  Unit = 'MB' }
    [pscustomobject]@{ Metric = 'IdlePrivateMb';    Label = 'Idle private bytes';        Target = 180;  Fail = 320;  Unit = 'MB' }
    [pscustomobject]@{ Metric = 'ManagedHeapMb';    Label = 'Managed heap at first frame'; Target = 40; Fail = 80;   Unit = 'MB' }
    [pscustomobject]@{ Metric = 'ShutdownMs';       Label = 'Graceful shutdown';         Target = 1500; Fail = 4000; Unit = 'ms' }
)

# --- helpers -----------------------------------------------------------------

function Get-Median {
    param([double[]] $Values)
    if (-not $Values -or $Values.Count -eq 0) { return $null }
    # @() matters: Sort-Object unwraps a one-element pipeline to a scalar, and a scalar has no
    # .Count under StrictMode. A single cold run is the normal case, so this is not an edge case.
    $sorted = @($Values | Sort-Object)
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return [double]$sorted[$mid] }
    return ([double]$sorted[$mid - 1] + [double]$sorted[$mid]) / 2
}

function Get-Percentile {
    param([double[]] $Values, [double] $Percentile)
    if (-not $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    # Nearest-rank: with 5 samples p95 is simply the max, which is the honest answer at this
    # sample size — interpolating would invent precision the run doesn't have.
    $rank = [int][math]::Ceiling(($Percentile / 100.0) * $sorted.Count)
    if ($rank -lt 1) { $rank = 1 }
    return [double]$sorted[$rank - 1]
}

function Stop-RunningInstances {
    # The app is single-instance: a second launch redirects to the running one and exits
    # immediately, which would otherwise be recorded as an absurdly fast startup.
    $running = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }
    Write-Verbose "Stopping $($running.Count) running instance(s) before measuring"
    foreach ($p in $running) {
        try { $p.Kill() } catch { }
    }
    foreach ($p in $running) {
        try { $null = $p.WaitForExit(5000) } catch { }
    }
}

function Request-AppExit {
    <#
        Signals the one-shot named event armed by StartupTelemetry. The callback invokes the same
        MainWindow.ExitApplication path as File > Exit; no tray implementation detail is assumed.

        Setting the event cannot tell us the app ever opened it — a failure to arm is only visible
        in the app's own log — so this returns nothing and the WaitForExit that follows is what
        decides whether the shutdown was graceful.
    #>
    param([Threading.EventWaitHandle] $ExitEvent)
    $null = $ExitEvent.Set()
}

function Read-StartupReport {
    <#
        Reads the app's atomic startup report.

        Retried, always: the app rewrites this file as each readiness milestone lands, and each
        rewrite is a File.Move. Test-Path can see the destination the instant the move creates it,
        a moment before the move releases its handle, so any single read can fail with a sharing
        violation even though the document on disk is complete. With $ErrorActionPreference = Stop
        an unretried read would throw away a whole iteration for a transient miss.
    #>
    param([string] $Path)

    foreach ($attempt in 1..10) {
        try {
            return Get-Content $Path -Raw -ErrorAction Stop | ConvertFrom-Json
        } catch {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Milliseconds 100
        }
    }
}

$script:StandbyPurgeAvailable = $null
function Clear-StandbyList {
    # Purging the standby list is what makes a repeat launch genuinely cold: otherwise the app's
    # binaries stay in the OS file cache and every run after the first measures warm I/O.
    if ($null -eq $script:StandbyPurgeAvailable) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        $script:StandbyPurgeAvailable = $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)

        if ($script:StandbyPurgeAvailable) {
            if (-not ('Perf.MemoryList' -as [type])) {
                Add-Type -Namespace Perf -Name MemoryList -MemberDefinition @'
[DllImport("ntdll.dll")]
public static extern int NtSetSystemInformation(int InfoClass, ref int Info, int Length);

[StructLayout(LayoutKind.Sequential)]
private struct LUID { public uint LowPart; public int HighPart; }

[StructLayout(LayoutKind.Sequential)]
private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

[DllImport("advapi32.dll", SetLastError = true)]
private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
private static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);

[DllImport("advapi32.dll", SetLastError = true)]
private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
    ref TOKEN_PRIVILEGES newState, uint length, IntPtr previous, IntPtr returnLength);

[DllImport("kernel32.dll")]
private static extern IntPtr GetCurrentProcess();

[DllImport("kernel32.dll", SetLastError = true)]
private static extern bool CloseHandle(IntPtr handle);

// Being in the Administrators group only means the token *holds* a privilege; it is disabled
// until something enables it. Without this the purge returns STATUS_PRIVILEGE_NOT_HELD
// (0xC0000061) from an elevated shell, which reads like a policy block but is not one.
public static bool EnablePrivilege(string name)
{
    IntPtr token;
    // TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY
    if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out token)) return false;
    try
    {
        LUID luid;
        if (!LookupPrivilegeValue(null, name, out luid)) return false;

        TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
        tp.PrivilegeCount = 1;
        tp.Luid = luid;
        tp.Attributes = 0x0002; // SE_PRIVILEGE_ENABLED
        if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;

        // AdjustTokenPrivileges reports success even when it assigned nothing; only the last
        // error distinguishes "enabled" from "not all privileges were assigned".
        return Marshal.GetLastWin32Error() == 0;
    }
    finally { CloseHandle(token); }
}
'@
            }

            if (-not [Perf.MemoryList]::EnablePrivilege('SeProfileSingleProcessPrivilege')) {
                $script:StandbyPurgeAvailable = $false
                Write-Warning 'SeProfileSingleProcessPrivilege could not be enabled; the standby list cannot be purged.'
            }
        } else {
            Write-Warning 'Standby-list purge needs an elevated shell; runs will be warm.'
        }
    }

    if (-not $script:StandbyPurgeAvailable) { return $false }
    try {
        # SystemMemoryListInformation (80) / MemoryPurgeStandbyList (4)
        $command = 4
        $status = [Perf.MemoryList]::NtSetSystemInformation(80, [ref] $command, 4)
        if ($status -ne 0) {
            $hint = if ($status -eq -1073741727) {  # 0xC0000061 STATUS_PRIVILEGE_NOT_HELD
                ' (STATUS_PRIVILEGE_NOT_HELD — SeProfileSingleProcessPrivilege is held but not enabled)'
            } else { '' }
            Write-Warning ("Standby-list purge returned NTSTATUS 0x{0:X8}.{1}" -f $status, $hint)
            return $false
        }
        return $true
    } catch {
        $script:StandbyPurgeAvailable = $false
        Write-Warning "Standby-list purge failed: $($_.Exception.Message)"
        return $false
    }
}

function Invoke-MeasuredLaunch {
    param(
        [string] $ExePath,
        [int]    $Index,
        [string] $ReportPath,
        [string] $DataRoot,
        [string] $FixturePath,
        [bool]   $Cold,
        [bool]   $ConversationRequired
    )

    Stop-RunningInstances
    if (Test-Path $ReportPath) { Remove-Item $ReportPath -Force }
    if ($DataRoot -and (Test-Path $DataRoot)) { Remove-Item $DataRoot -Recurse -Force }
    if ($DataRoot -and $FixturePath) {
        $null = New-Item -ItemType Directory -Force -Path $DataRoot
        Copy-Item -Path (Join-Path $FixturePath '*') -Destination $DataRoot -Recurse -Force
    }

    if ($Cold -and -not (Clear-StandbyList)) {
        throw 'Cold mode requires an elevated shell and a successful standby-list purge. No sample was recorded.'
    }

    $exitEventName = "Local\ConnectOnion.PerfExit.$([guid]::NewGuid().ToString('N'))"
    $exitEvent = [Threading.EventWaitHandle]::new(
        $false, [Threading.EventResetMode]::AutoReset, $exitEventName)
    $env:CONNECTONION_PERF_OUT = $ReportPath
    $env:CONNECTONION_PERF_EXIT_EVENT = $exitEventName
    if ($ConversationRequired) { $env:CONNECTONION_PERF_OPEN_CONVERSATION = '1' }
    else { Remove-Item Env:CONNECTONION_PERF_OPEN_CONVERSATION -ErrorAction SilentlyContinue }
    if ($DataRoot) { $env:CONNECTONION_DATA_ROOT = $DataRoot }
    else { Remove-Item Env:CONNECTONION_DATA_ROOT -ErrorAction SilentlyContinue }

    $proc = Start-Process -FilePath $ExePath -PassThru
    try {
        # The app writes its report atomically at first frame; polling for the file is more
        # reliable than watching for a window handle, which appears before anything is drawn.
        $deadline = (Get-Date).AddSeconds($LaunchTimeoutSeconds)
        while (-not (Test-Path $ReportPath)) {
            if ($proc.HasExited) {
                throw "Iteration $Index exited (code $($proc.ExitCode)) before reporting startup."
            }
            if ((Get-Date) -gt $deadline) {
                throw "Iteration $Index did not reach first frame within $LaunchTimeoutSeconds s."
            }
            Start-Sleep -Milliseconds 50
        }

        # Readiness gets its own budget rather than whatever is left of the launch deadline: a
        # slow first frame would otherwise silently shrink the window the milestones have to
        # land in, and the run would fail on a timeout that says nothing about readiness.
        $readinessDeadline = (Get-Date).AddSeconds($ReadinessTimeoutSeconds)
        $requiredMarks = @('firstFrame', 'firstInteractive', 'sessionListLoaded', 'shellInitialized')
        if ($ConversationRequired) { $requiredMarks += 'firstConversationRendered' }
        $report = $null
        while ($true) {
            $report = Read-StartupReport -Path $ReportPath
            $observed = @($report.marks | ForEach-Object { $_.phase })
            $missing = @($requiredMarks | Where-Object { $_ -notin $observed })
            if ($missing.Count -eq 0) { break }
            if ((Get-Date) -gt $readinessDeadline) {
                throw "Iteration $Index did not reach readiness milestones within $ReadinessTimeoutSeconds s; missing: $($missing -join ', ')."
            }
            Start-Sleep -Milliseconds 50
        }

        # Readiness says the shell is usable; it does not say startup work has drained. Presence
        # probes and lazy first-use allocations are still in flight, so idle memory is sampled
        # after a settling period — the same one the ratified baseline was measured with.
        Start-Sleep -Seconds $SettleSeconds
        $proc.Refresh()

        # Say so plainly when the process is gone. Someone closing the window mid-settle is the
        # common cause, and without this check the run dies on the memory reads below with
        # "The property 'Count' cannot be found on this object" — a StrictMode artifact of
        # querying an exited process, which says nothing about what actually happened.
        if ($proc.HasExited) {
            throw "Iteration $Index : the app exited (code $($proc.ExitCode)) before it could be sampled. If you closed the window, just re-run; the benchmark needs the process alive through the ${SettleSeconds}s settle."
        }

        $idleWorkingSet = $proc.WorkingSet64
        $idlePrivate = $proc.PrivateMemorySize64
        $peakWorkingSet = $proc.PeakWorkingSet64
        $handles = $proc.HandleCount
        $threads = $proc.Threads.Count

        # Ask for the real exit path rather than killing it: shutdown drains runs and stops the
        # Generic Host, and that cost is part of the app's felt performance too.
        Request-AppExit -ExitEvent $exitEvent

        $shutdown = [Diagnostics.Stopwatch]::StartNew()
        $exited = $proc.WaitForExit(15000)
        $shutdown.Stop()
        if (-not $exited) {
            Write-Warning "Iteration $Index did not exit within 15 s of the graceful-exit request; killing. Check the app log for 'Performance exit event ... could not be armed'."
            try { $proc.Kill() } catch { }
        }

        $marks = @{}
        foreach ($mark in $report.marks) { $marks[$mark.phase] = [double]$mark.elapsedMs }

        return [pscustomobject]@{
            Iteration        = $Index
            Mode             = $(if ($Cold) { 'cold' } else { 'warm' })
            Configuration    = $report.configuration
            Packaged         = $report.packaged
            TimeToFirstFrameMs = $marks['firstFrame']
            ManagedEntryMs   = $marks['managedEntry']
            HostStartedMs    = $marks['hostStarted']
            WindowCreatedMs  = $marks['windowCreated']
            WindowActivatedMs = $marks['windowActivated']
            FirstFrameWorkingSetMb = [math]::Round($report.memory.workingSetBytes / 1MB, 1)
            FirstInteractiveMs = $marks['firstInteractive']
            SessionListLoadedMs = $marks['sessionListLoaded']
            FirstConversationRenderedMs = $marks['firstConversationRendered']
            ShellInitializedMs = $marks['shellInitialized']
            ManagedHeapMb    = [math]::Round($report.memory.managedHeapBytes / 1MB, 1)
            IdleWorkingSetMb = [math]::Round($idleWorkingSet / 1MB, 1)
            IdlePrivateMb    = [math]::Round($idlePrivate / 1MB, 1)
            PeakWorkingSetMb = [math]::Round($peakWorkingSet / 1MB, 1)
            HandleCount      = $handles
            ThreadCount      = $threads
            ShutdownMs       = [math]::Round($shutdown.Elapsed.TotalMilliseconds, 0)
            GracefulExit     = $exited
        }
    } finally {
        if (-not $proc.HasExited) { try { $proc.Kill() } catch { } }
        Remove-Item Env:CONNECTONION_PERF_OUT -ErrorAction SilentlyContinue
        Remove-Item Env:CONNECTONION_PERF_EXIT_EVENT -ErrorAction SilentlyContinue
        Remove-Item Env:CONNECTONION_PERF_OPEN_CONVERSATION -ErrorAction SilentlyContinue
        Remove-Item Env:CONNECTONION_DATA_ROOT -ErrorAction SilentlyContinue
        $exitEvent.Dispose()
    }
}

function Format-OptionalMilliseconds {
    param($Value)
    if ($null -eq $Value) { return '' }
    return [math]::Round([double]$Value, 0)
}

function Get-Stats {
    param([string] $Name, [object[]] $Values, [string] $Unit)
    # Keep optional milestones optional. Binding directly to [double[]] converts a missing
    # conversation mark ($null) to 0.0 and fabricates a zero-millisecond render in the report.
    $clean = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ })
    if ($clean.Count -eq 0) { return $null }
    return [pscustomobject]@{
        Metric = $Name
        Unit   = $Unit
        Count  = $clean.Count
        Median = [math]::Round((Get-Median $clean), 1)
        Min    = [math]::Round(($clean | Measure-Object -Minimum).Minimum, 1)
        Max    = [math]::Round(($clean | Measure-Object -Maximum).Maximum, 1)
        P95    = [math]::Round((Get-Percentile $clean 95), 1)
    }
}

# --- resolve inputs ----------------------------------------------------------

$packageFullName = $null
if ($Mode -like '*Msix') {
    # Sort on [version], not the string Get-AppxPackage hands back: lexically "1.0.9.0" sorts
    # above "1.0.10.0", so a string sort silently benchmarks an older side-by-side install.
    $package = Get-AppxPackage -Name $PackageName |
        Sort-Object { [version] $_.Version } -Descending |
        Select-Object -First 1
    if (-not $package) { throw "Installed MSIX package '$PackageName' was not found." }
    $packageFullName = $package.PackageFullName
    $Exe = Join-Path $package.InstallLocation $PackageExecutable
} elseif (-not $Exe) {
    $candidates = @(
        "$RepoRoot\ConnectOnion.WinUIClient\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\$ProcessName.exe"
        "$RepoRoot\ConnectOnion.WinUIClient\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\$ProcessName.exe"
    )
    $Exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $Exe -or -not (Test-Path $Exe)) {
    throw @"
No app executable found. Build one first:
  dotnet build ConnectOnion.WinUIClient\ConnectOnion.WinUIClient.sln -c Release -p:Platform=x64
"@
}
$Exe = (Resolve-Path $Exe).Path
if ($Exe -match '\\Debug\\' -and -not $AllowDebugBuild) {
    throw "Refusing to benchmark a Debug build ($Exe). Build Release, or pass -AllowDebugBuild."
}

if (-not $OutDir) {
    $OutDir = Join-Path $RepoRoot ("TestResults\perf\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$null = New-Item -ItemType Directory -Force -Path $OutDir

$dataRoot = $null
if (-not $UseRealDataRoot) { $dataRoot = Join-Path $OutDir 'data-root' }
if ($FixturePath) {
    $FixturePath = (Resolve-Path $FixturePath).Path
    if ($UseRealDataRoot) { throw '-FixturePath cannot be combined with -UseRealDataRoot.' }
}

Write-Host "Executable : $Exe"
Write-Host "Mode       : $Mode"
Write-Host "Iterations : $Iterations (minimum valid sample count: 5, settle ${SettleSeconds}s)"
Write-Host "Data root  : $(if ($dataRoot) { $dataRoot } else { 'real profile (%AppData%\ConnectOnion)' })"
Write-Host "Output     : $OutDir"
Write-Host ''

# --- run ---------------------------------------------------------------------

$results = @()
if ($Mode -like 'Warm*') {
    Write-Host 'Warm-up (not recorded) ... ' -NoNewline
    $warmup = Invoke-MeasuredLaunch -ExePath $Exe -Index 0 -DataRoot $dataRoot `
        -FixturePath $FixturePath -Cold $false `
        -ReportPath (Join-Path $OutDir 'warmup.json') `
        -ConversationRequired $RequireConversation.IsPresent
    if (-not $warmup.GracefulExit) { throw 'Warm-up did not exit gracefully.' }
    Write-Host 'complete'
}
for ($i = 1; $i -le $Iterations; $i++) {
    # Without a standby purge only the first launch is meaningfully cold, so later iterations
    # are labelled warm and aggregated separately rather than averaged together.
    $cold = $Mode -like 'Cold*'
    Write-Host ("Iteration {0}/{1} ({2}) ..." -f $i, $Iterations, $(if ($cold) { 'cold' } else { 'warm' })) -NoNewline
    $result = Invoke-MeasuredLaunch -ExePath $Exe -Index $i -DataRoot $dataRoot -FixturePath $FixturePath -Cold $cold `
        -ReportPath (Join-Path $OutDir "startup-$i.json") -ConversationRequired $RequireConversation.IsPresent
    $expectedPackaged = $Mode -like '*Msix'
    if ([bool]$result.Packaged -ne $expectedPackaged) {
        throw "Mode $Mode expected packaged=$expectedPackaged, but the app reported packaged=$($result.Packaged)."
    }
    $results += $result
    Write-Host (" {0} ms to first frame, {1} MB idle" -f `
        [math]::Round($result.TimeToFirstFrameMs, 0), $result.IdleWorkingSetMb)
}

Stop-RunningInstances

# --- aggregate ---------------------------------------------------------------

$coldRuns = @($results | Where-Object { $_.Mode -eq 'cold' })
$warmRuns = @($results | Where-Object { $_.Mode -eq 'warm' })

$stats = @()
$stats += Get-Stats 'ColdStartMs'      @($coldRuns | ForEach-Object { $_.TimeToFirstFrameMs }) 'ms'
$stats += Get-Stats 'WarmStartMs'      @($warmRuns | ForEach-Object { $_.TimeToFirstFrameMs }) 'ms'
$stats += Get-Stats 'FirstInteractiveMs' @($results | ForEach-Object { $_.FirstInteractiveMs }) 'ms'
$stats += Get-Stats 'SessionListLoadedMs' @($results | ForEach-Object { $_.SessionListLoadedMs }) 'ms'
$stats += Get-Stats 'FirstConversationRenderedMs' @($results | ForEach-Object { $_.FirstConversationRenderedMs }) 'ms'
$stats += Get-Stats 'ShellInitializedMs' @($results | ForEach-Object { $_.ShellInitializedMs }) 'ms'
$stats += Get-Stats 'IdleWorkingSetMb' @($results | ForEach-Object { $_.IdleWorkingSetMb }) 'MB'
$stats += Get-Stats 'IdlePrivateMb'    @($results | ForEach-Object { $_.IdlePrivateMb }) 'MB'
$stats += Get-Stats 'ManagedHeapMb'    @($results | ForEach-Object { $_.ManagedHeapMb }) 'MB'
$stats += Get-Stats 'PeakWorkingSetMb' @($results | ForEach-Object { $_.PeakWorkingSetMb }) 'MB'
# Only graceful exits carry a real shutdown duration — a killed run reports the wait timeout,
# which would drag the median toward the timeout rather than toward the app's behaviour. The
# count of non-graceful exits is reported separately instead of being averaged in.
$gracefulRuns = @($results | Where-Object { $_.GracefulExit })
if ($gracefulRuns.Count -lt 5) {
    throw "Only $($gracefulRuns.Count) graceful shutdown sample(s) were valid; at least five are required."
}
$stats += Get-Stats 'ShutdownMs'       @($gracefulRuns | ForEach-Object { $_.ShutdownMs }) 'ms'
$stats = @($stats | Where-Object { $null -ne $_ })

$verdicts = @()
foreach ($budget in $Budgets) {
    $stat = $stats | Where-Object { $_.Metric -eq $budget.Metric } | Select-Object -First 1
    if (-not $stat) { continue }
    # A traced run measures the app plus the tracer. Observed ETW overhead on this benchmark is
    # ~20% on launch and ~70% on shutdown, which is enough to push a healthy build past a target
    # and print a regression that does not exist. Report the numbers, withhold the verdict.
    $status = if ($UnderInstrumentation) { 'n/a (traced)' }
        elseif ($stat.Median -gt $budget.Fail) { 'FAIL' }
        elseif ($stat.Median -gt $budget.Target) { 'OVER TARGET' }
        else { 'PASS' }
    $verdicts += [pscustomobject]@{
        Metric = $budget.Metric; Label = $budget.Label; Unit = $budget.Unit
        Median = $stat.Median; Target = $budget.Target; Fail = $budget.Fail; Status = $status
    }
}

$buildCommit = (git -C $RepoRoot rev-parse HEAD).Trim()
$buildDirty = [bool](git -C $RepoRoot status --porcelain)
Add-Type -AssemblyName System.Windows.Forms
$machine = [pscustomobject]@{
    Machine       = $env:COMPUTERNAME
    OS            = (Get-CimInstance Win32_OperatingSystem).Caption
    OSVersion     = [Environment]::OSVersion.Version.ToString()
    Cores         = [Environment]::ProcessorCount
    TotalMemoryGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
    Configuration = $results[0].Configuration
    Packaged      = $results[0].Packaged
    # True iff every measured sample was preceded by a successful purge. Cold modes guarantee
    # that by throwing on the first failure, so the mode is the honest answer here;
    # $script:StandbyPurgeAvailable only records whether the shell *could* purge.
    StandbyPurged = [bool]($Mode -like 'Cold*')
    ColdCacheQualification = $(if ($Mode -like 'Cold*') { 'Qualified-PurgedBeforeEverySample' } else { 'NotApplicable-WarmMode' })
    BenchmarkMode = $Mode
    UnderInstrumentation = [bool]$UnderInstrumentation
    BuildCommit = $buildCommit
    BuildDirty = $buildDirty
    # Provenance, not measurement: hashing an MSIX payload under C:\Program Files\WindowsApps can
    # be denied by that tree's ACLs, and losing a completed five-sample run over a metadata field
    # would be absurd. Record the failure in the field instead.
    ExecutableSha256 = $(
        try { (Get-FileHash -Algorithm SHA256 -LiteralPath $Exe -ErrorAction Stop).Hash }
        catch { "unavailable: $($_.Exception.Message)" })
    PackageFullName = $packageFullName
    Dataset = $DatasetId
    PowerScheme = ((powercfg /getactivescheme) -join ' ').Trim()
    PowerSource = [System.Windows.Forms.SystemInformation]::PowerStatus.PowerLineStatus.ToString()
    NonGracefulExits = $results.Count - $gracefulRuns.Count
    Executable    = $Exe
    CapturedUtc   = (Get-Date).ToUniversalTime().ToString('o')
}

# --- report ------------------------------------------------------------------

$jsonPath = Join-Path $OutDir 'results.json'
[pscustomobject]@{
    environment = $machine; budgets = $verdicts; statistics = $stats; iterations = $results
} | ConvertTo-Json -Depth 6 | Set-Content -Path $jsonPath -Encoding utf8

$md = New-Object Text.StringBuilder
$null = $md.AppendLine('# Performance benchmark')
$null = $md.AppendLine()
$null = $md.AppendLine("Captured $($machine.CapturedUtc) on $($machine.Machine) — $($machine.OS) $($machine.OSVersion), $($machine.Cores) cores, $($machine.TotalMemoryGb) GB RAM.")
$null = $md.AppendLine()
$null = $md.AppendLine("Build: **$($machine.Configuration)**, mode: $Mode, packaged: $($machine.Packaged), standby list purged before every sample: $($machine.StandbyPurged).")
# No "purge failed" caveat: a cold mode that could not purge throws before any sample is
# recorded, so a report exists only when the claim above is true.
if ($machine.NonGracefulExits -gt 0) {
    $null = $md.AppendLine()
    $null = $md.AppendLine("> $($machine.NonGracefulExits) of $($results.Count) iteration(s) had to be killed after the named graceful-exit request; those are excluded from the shutdown statistic.")
}
$null = $md.AppendLine()
$null = $md.AppendLine('## Budgets')
if ($UnderInstrumentation) {
    $null = $md.AppendLine()
    $null = $md.AppendLine('> **Captured inside a WPR trace — these numbers are not budget numbers.** ETW instrumentation measured ~20% on launch and ~70% on shutdown against an untraced run of the same binary, so verdicts are withheld. Use this report to locate where the time goes, and an untraced run to decide whether a build passes.')
}
$null = $md.AppendLine()
$null = $md.AppendLine('| Metric | Median | Target | Fail | Status |')
$null = $md.AppendLine('|---|---:|---:|---:|---|')
foreach ($v in $verdicts) {
    $null = $md.AppendLine("| $($v.Label) | $($v.Median) $($v.Unit) | $($v.Target) $($v.Unit) | $($v.Fail) $($v.Unit) | $($v.Status) |")
}
$null = $md.AppendLine()
$null = $md.AppendLine('## Distribution')
$null = $md.AppendLine()
$null = $md.AppendLine('| Metric | n | Median | Min | Max | P95 |')
$null = $md.AppendLine('|---|---:|---:|---:|---:|---:|')
foreach ($s in $stats) {
    $null = $md.AppendLine("| $($s.Metric) ($($s.Unit)) | $($s.Count) | $($s.Median) | $($s.Min) | $($s.Max) | $($s.P95) |")
}
$null = $md.AppendLine()
$null = $md.AppendLine('## Launch phase breakdown')
$null = $md.AppendLine()
$null = $md.AppendLine('Milliseconds since process start. The gap before `managedEntry` is CLR + Windows App SDK bootstrap.')
$null = $md.AppendLine()
$null = $md.AppendLine('| # | Mode | managedEntry | hostStarted | windowCreated | windowActivated | firstFrame | interactive | sessions | conversation | shell |')
$null = $md.AppendLine('|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|')
foreach ($r in $results) {
    $null = $md.AppendLine("| $($r.Iteration) | $($r.Mode) | $(Format-OptionalMilliseconds $r.ManagedEntryMs) | $(Format-OptionalMilliseconds $r.HostStartedMs) | $(Format-OptionalMilliseconds $r.WindowCreatedMs) | $(Format-OptionalMilliseconds $r.WindowActivatedMs) | $(Format-OptionalMilliseconds $r.TimeToFirstFrameMs) | $(Format-OptionalMilliseconds $r.FirstInteractiveMs) | $(Format-OptionalMilliseconds $r.SessionListLoadedMs) | $(Format-OptionalMilliseconds $r.FirstConversationRenderedMs) | $(Format-OptionalMilliseconds $r.ShellInitializedMs) |")
}
$null = $md.AppendLine()
$null = $md.AppendLine('## Per-iteration resources')
$null = $md.AppendLine()
$null = $md.AppendLine("Memory sampled ${SettleSeconds}s after all required readiness milestones completed.")
$null = $md.AppendLine()
$null = $md.AppendLine('| # | Idle WS (MB) | Idle private (MB) | Peak WS (MB) | Managed heap (MB) | Handles | Threads | Shutdown (ms) |')
$null = $md.AppendLine('|---:|---:|---:|---:|---:|---:|---:|---:|')
foreach ($r in $results) {
    $null = $md.AppendLine("| $($r.Iteration) | $($r.IdleWorkingSetMb) | $($r.IdlePrivateMb) | $($r.PeakWorkingSetMb) | $($r.ManagedHeapMb) | $($r.HandleCount) | $($r.ThreadCount) | $($r.ShutdownMs) |")
}

$mdPath = Join-Path $OutDir 'report.md'
$md.ToString() | Set-Content -Path $mdPath -Encoding utf8

Write-Host ''
$verdicts | Format-Table Label, @{ N = 'Median'; E = { "$($_.Median) $($_.Unit)" } }, Status -AutoSize
Write-Host "Report : $mdPath"
Write-Host "Raw    : $jsonPath"

$failed = @($verdicts | Where-Object { $_.Status -eq 'FAIL' })
if ($failed.Count -gt 0) {
    Write-Warning "$($failed.Count) metric(s) over the failure threshold: $($failed.Metric -join ', ')"
    if ($EnforceBudgets) { exit 1 }
}
exit 0
