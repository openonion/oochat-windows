<#
.SYNOPSIS
    Samples Release-process resource usage while the real WinUI window is idle.

.DESCRIPTION
    Launches the x64 unpackaged Release executable, waits for a responsive top-level window,
    then records CPU, memory, handles, threads, GUI objects, process I/O, and owned TCP
    connections at a fixed interval. The app is stopped after the capture; shutdown latency is
    deliberately not reported because this harness does not drive the tray Exit command.
#>
[CmdletBinding()]
param(
    [string] $Exe,
    [int] $ProcessId,
    [string] $DataRoot,
    [int] $DurationSeconds = 300,
    [int] $SampleSeconds = 5,
    [string] $OutDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Exe) {
    $Exe = Join-Path $repoRoot `
        'ConnectOnion.WinUIClient\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\ConnectOnion.WinUIClient.exe'
}
if (-not (Test-Path -LiteralPath $Exe)) {
    throw "Release executable not found: $Exe"
}
$Exe = (Resolve-Path -LiteralPath $Exe).Path
if ($Exe -notmatch '\\Release\\' -or $Exe -notmatch '\\win-x64\\') {
    throw "Idle measurements require the Release x64 executable: $Exe"
}
if ($DurationSeconds -lt 120) { throw '-DurationSeconds must be at least 120.' }
if ($SampleSeconds -lt 1) { throw '-SampleSeconds must be positive.' }
if (-not $OutDir) {
    $OutDir = Join-Path $repoRoot ("TestResults\idle\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$null = New-Item -ItemType Directory -Path $OutDir -Force

if (-not ('IdlePerf.NativeMethods' -as [type])) {
    Add-Type -Namespace IdlePerf -Name NativeMethods -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern bool GetProcessIoCounters(
    System.IntPtr processHandle,
    out IoCounters counters);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern uint GetGuiResources(System.IntPtr processHandle, uint flags);
'@
}

function Get-IoCounters {
    param([Diagnostics.Process] $Process)
    $counters = New-Object IdlePerf.NativeMethods+IoCounters
    if (-not [IdlePerf.NativeMethods]::GetProcessIoCounters($Process.Handle, [ref] $counters)) {
        throw "GetProcessIoCounters failed for PID $($Process.Id)."
    }
    return $counters
}

function Get-TcpConnectionCount {
    param([int] $ProcessId)
    try {
        return @(Get-NetTCPConnection -OwningProcess $ProcessId -ErrorAction Stop).Count
    } catch {
        return $null
    }
}

function Get-LinearSlope {
    param([double[]] $Values, [double] $SecondsPerSample)
    if ($Values.Count -lt 2) { return 0 }
    $xMean = (($Values.Count - 1) * $SecondsPerSample) / 2.0
    $yMean = ($Values | Measure-Object -Average).Average
    $numerator = 0.0
    $denominator = 0.0
    for ($index = 0; $index -lt $Values.Count; $index++) {
        $x = $index * $SecondsPerSample
        $dx = $x - $xMean
        $numerator += $dx * ($Values[$index] - $yMean)
        $denominator += $dx * $dx
    }
    if ($denominator -eq 0) { return 0 }
    return $numerator / $denominator
}

$savedDataRoot = [Environment]::GetEnvironmentVariable('CONNECTONION_DATA_ROOT', 'Process')
if ($DataRoot) {
    $resolvedDataRoot = [IO.Path]::GetFullPath($DataRoot)
    $null = New-Item -ItemType Directory -Path $resolvedDataRoot -Force
    $env:CONNECTONION_DATA_ROOT = $resolvedDataRoot
}

$process = $null
try {
    # A background helper can attach to an app launched by the foreground controller. This avoids
    # inheriting the helper's hidden STARTUPINFO into the WinUI process.
    if ($ProcessId -gt 0) {
        $process = [Diagnostics.Process]::GetProcessById($ProcessId)
    } else {
        $process = Start-Process -FilePath $Exe -WindowStyle Normal -PassThru
    }
    $deadline = (Get-Date).AddSeconds(60)
    do {
        if ($process.HasExited) {
            throw "App exited with code $($process.ExitCode) before a window became ready."
        }
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    } until (
        ($process.MainWindowHandle -ne [IntPtr]::Zero -and $process.Responding) -or
        (Get-Date) -gt $deadline
    )
    if ($process.MainWindowHandle -eq [IntPtr]::Zero -or -not $process.Responding) {
        throw 'App did not expose a responsive top-level window within 60 seconds.'
    }

    $sampleCount = [int][math]::Floor($DurationSeconds / $SampleSeconds) + 1
    $samples = [Collections.Generic.List[object]]::new($sampleCount)
    $previousCpu = $process.TotalProcessorTime
    $previousTime = [DateTime]::UtcNow
    $previousIo = Get-IoCounters $process

    for ($index = 0; $index -lt $sampleCount; $index++) {
        if ($index -gt 0) { Start-Sleep -Seconds $SampleSeconds }
        $process.Refresh()
        if ($process.HasExited) { throw 'App exited during the idle capture.' }

        $now = [DateTime]::UtcNow
        $cpu = $process.TotalProcessorTime
        $io = Get-IoCounters $process
        $elapsedSeconds = [math]::Max(($now - $previousTime).TotalSeconds, 0.001)
        $cpuPercent = (($cpu - $previousCpu).TotalSeconds / $elapsedSeconds) /
            [Environment]::ProcessorCount * 100.0

        $samples.Add([pscustomobject]@{
            Sample              = $index
            ElapsedSeconds      = [math]::Round($index * $SampleSeconds, 1)
            CpuPercent          = [math]::Round($cpuPercent, 3)
            WorkingSetMb        = [math]::Round($process.WorkingSet64 / 1MB, 2)
            PrivateBytesMb      = [math]::Round($process.PrivateMemorySize64 / 1MB, 2)
            PagedMemoryMb       = [math]::Round($process.PagedMemorySize64 / 1MB, 2)
            NonpagedSystemMb    = [math]::Round($process.NonpagedSystemMemorySize64 / 1MB, 2)
            Handles             = $process.HandleCount
            Threads             = $process.Threads.Count
            GdiObjects          = [IdlePerf.NativeMethods]::GetGuiResources($process.Handle, 0)
            UserObjects         = [IdlePerf.NativeMethods]::GetGuiResources($process.Handle, 1)
            ReadBytesDelta      = [long]($io.ReadTransferCount - $previousIo.ReadTransferCount)
            WriteBytesDelta     = [long]($io.WriteTransferCount - $previousIo.WriteTransferCount)
            OtherBytesDelta     = [long]($io.OtherTransferCount - $previousIo.OtherTransferCount)
            TcpConnections      = Get-TcpConnectionCount $process.Id
        })

        $previousCpu = $cpu
        $previousTime = $now
        $previousIo = $io
    }

    $csvPath = Join-Path $OutDir 'samples.csv'
    $samples | Export-Csv -Path $csvPath -NoTypeInformation -Encoding utf8

    $tail = @($samples | Select-Object -Skip ([int][math]::Floor($samples.Count / 2)))
    $summary = [pscustomobject]@{
        Executable                  = $Exe
        Configuration               = 'Release'
        Platform                    = 'x64'
        Packaged                    = $false
        ProcessId                   = $process.Id
        MainWindowHandle            = $process.MainWindowHandle.ToInt64()
        Responsive                  = $process.Responding
        DurationSeconds             = $DurationSeconds
        SampleSeconds               = $SampleSeconds
        Samples                     = $samples.Count
        MeanTailCpuPercent          = [math]::Round(
            ($tail.CpuPercent | Measure-Object -Average).Average, 3)
        WorkingSetChangeMb          = [math]::Round(
            $samples[-1].WorkingSetMb - $samples[0].WorkingSetMb, 2)
        PrivateBytesChangeMb        = [math]::Round(
            $samples[-1].PrivateBytesMb - $samples[0].PrivateBytesMb, 2)
        PrivateBytesTailSlopeMbMin  = [math]::Round(
            (Get-LinearSlope @($tail.PrivateBytesMb) $SampleSeconds) * 60, 3)
        HandleChange                = $samples[-1].Handles - $samples[0].Handles
        HandleTailSpan              = ($tail.Handles | Measure-Object -Maximum).Maximum -
            ($tail.Handles | Measure-Object -Minimum).Minimum
        ThreadChange                = $samples[-1].Threads - $samples[0].Threads
        ThreadTailSpan              = ($tail.Threads | Measure-Object -Maximum).Maximum -
            ($tail.Threads | Measure-Object -Minimum).Minimum
        TotalReadBytes              = [long](($samples.ReadBytesDelta | Measure-Object -Sum).Sum)
        TotalWriteBytes             = [long](($samples.WriteBytesDelta | Measure-Object -Sum).Sum)
        TotalOtherBytes             = [long](($samples.OtherBytesDelta | Measure-Object -Sum).Sum)
        CapturedUtc                 = [DateTime]::UtcNow.ToString('o')
    }
    $summary | ConvertTo-Json | Set-Content -Path (Join-Path $OutDir 'summary.json') -Encoding utf8

    $summary | Format-List
    Write-Host "Samples: $csvPath"
} finally {
    if ($process -and -not $process.HasExited) {
        try { $process.Kill() } catch { }
    }
    [Environment]::SetEnvironmentVariable(
        'CONNECTONION_DATA_ROOT', $savedDataRoot, 'Process')
}
