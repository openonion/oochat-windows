<#
.SYNOPSIS
    Runs the project-wide headless suite plus real-window memory leak regression scenarios.

.DESCRIPTION
    Builds the unpackaged x64 app, optionally runs every headless test project, copies the local
    ConnectOnion profile to an isolated temporary data root, and uses FlaUI to repeatedly open and
    close Settings, Add Agent, Agent Detail, and a real conversation. The tail half of each sample
    series must plateau in Private Bytes, handles, and threads.

    The source profile is copied and never modified. The temporary copy is deleted in finally.
    A running ConnectOnion process is treated as an error because the app is single-instance.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-MemoryLeaks.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-MemoryLeaks.ps1 `
        -Cycles 24 -WarmupCycles 6 -SettleMilliseconds 750
#>
[CmdletBinding()]
param(
    [string] $Exe,
    [string] $SourceDataRoot,
    [string] $OutDir,
    [int] $Cycles = 24,
    [int] $WarmupCycles = 6,
    [int] $SettleMilliseconds = 750,
    [double] $MaxPrivateSlopeMb = 1.5,
    [double] $MaxPrivateTailSpanMb = 24.0,
    [int] $MaxHandleTailSpan = 64,
    [int] $MaxThreadTailSpan = 8,
    [ValidateSet('all', 'settings', 'add-agent', 'agent-detail', 'conversation', 'conversation-alternating')]
    [string[]] $Scenarios = @('all'),
    [switch] $AllowNoConversation,
    [switch] $SkipHeadless
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Exe) {
    $Exe = Join-Path $repoRoot 'ConnectOnion.WinUIClient\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ConnectOnion.WinUIClient.exe'
}
if (-not $SourceDataRoot) {
    $SourceDataRoot = Join-Path $env:APPDATA 'ConnectOnion'
}
if (-not $OutDir) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutDir = Join-Path $repoRoot "TestResults\memory\$stamp"
}

if ($Cycles -lt 8) { throw '-Cycles must be at least 8 so the tail slope is meaningful.' }
if ($WarmupCycles -lt 1) { throw '-WarmupCycles must be positive.' }
if ($SettleMilliseconds -lt 100) { throw '-SettleMilliseconds must be at least 100.' }

$running = @(Get-Process ConnectOnion.WinUIClient -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw 'Close ConnectOnion before running the memory suite; single-instance redirection invalidates samples.'
}

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$tempRoot = Join-Path $tempBase ("ConnectOnion-memory-" + [Guid]::NewGuid().ToString('N'))
$isolatedDataRoot = Join-Path $tempRoot 'data'
New-Item -ItemType Directory -Path $isolatedDataRoot -Force | Out-Null

$savedEnvironment = @{}
$environmentNames = @(
    'CONNECTONION_UI_TEST_EXE',
    'CONNECTONION_DATA_ROOT',
    'CONNECTONION_MEMORY_TEST',
    'CONNECTONION_MEMORY_CYCLES',
    'CONNECTONION_MEMORY_WARMUP_CYCLES',
    'CONNECTONION_MEMORY_SETTLE_MS',
    'CONNECTONION_MEMORY_MAX_PRIVATE_SLOPE_MB',
    'CONNECTONION_MEMORY_MAX_PRIVATE_TAIL_SPAN_MB',
    'CONNECTONION_MEMORY_MAX_HANDLE_TAIL_SPAN',
    'CONNECTONION_MEMORY_MAX_THREAD_TAIL_SPAN',
    'CONNECTONION_MEMORY_SCENARIOS',
    'CONNECTONION_MEMORY_REQUIRE_CONVERSATION'
)
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

$suiteSucceeded = $false
try {
    if (Test-Path $SourceDataRoot) {
        Copy-Item -Path (Join-Path $SourceDataRoot '*') -Destination $isolatedDataRoot -Recurse -Force
    } elseif (-not $AllowNoConversation) {
        throw "Source data root does not exist: $SourceDataRoot. Use -AllowNoConversation for shell-only coverage."
    }

    Invoke-DotNet build (Join-Path $repoRoot 'ConnectOnion.WinUIClient\ConnectOnion.WinUIClient.csproj') `
        --configuration Debug --no-restore '-p:Platform=x64' '-p:RunUnpackaged=true'
    Invoke-DotNet build (Join-Path $repoRoot 'tests\ConnectOnion.WinUIClient.UITests\ConnectOnion.WinUIClient.UITests.csproj') `
        --configuration Debug --no-restore

    if (-not $SkipHeadless) {
        Invoke-DotNet test (Join-Path $repoRoot 'tests\ConnectOnion.Protocol.Tests\ConnectOnion.Protocol.Tests.csproj') --no-restore
        Invoke-DotNet test (Join-Path $repoRoot 'tests\ConnectOnion.WinUIClient.UnitTests\ConnectOnion.WinUIClient.UnitTests.csproj') --no-restore
        Invoke-DotNet test (Join-Path $repoRoot 'tests\ConnectOnion.IntegrationTests\ConnectOnion.IntegrationTests.csproj') --no-restore
    }

    $env:CONNECTONION_UI_TEST_EXE = (Resolve-Path $Exe).Path
    $env:CONNECTONION_DATA_ROOT = $isolatedDataRoot
    $env:CONNECTONION_MEMORY_TEST = '1'
    $env:CONNECTONION_MEMORY_CYCLES = $Cycles.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:CONNECTONION_MEMORY_WARMUP_CYCLES = $WarmupCycles.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:CONNECTONION_MEMORY_SETTLE_MS = $SettleMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:CONNECTONION_MEMORY_MAX_PRIVATE_SLOPE_MB = $MaxPrivateSlopeMb.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:CONNECTONION_MEMORY_MAX_PRIVATE_TAIL_SPAN_MB = $MaxPrivateTailSpanMb.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:CONNECTONION_MEMORY_MAX_HANDLE_TAIL_SPAN = $MaxHandleTailSpan.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:CONNECTONION_MEMORY_MAX_THREAD_TAIL_SPAN = $MaxThreadTailSpan.ToString([Globalization.CultureInfo]::InvariantCulture)
    if ($Scenarios -notcontains 'all') { $env:CONNECTONION_MEMORY_SCENARIOS = $Scenarios -join ',' }
    else { Remove-Item Env:\CONNECTONION_MEMORY_SCENARIOS -ErrorAction SilentlyContinue }
    if ($AllowNoConversation) { $env:CONNECTONION_MEMORY_REQUIRE_CONVERSATION = '0' }
    else { $env:CONNECTONION_MEMORY_REQUIRE_CONVERSATION = '1' }

    $trxName = 'memory-leaks.trx'
    Invoke-DotNet test (Join-Path $repoRoot 'tests\ConnectOnion.WinUIClient.UITests\ConnectOnion.WinUIClient.UITests.csproj') `
        --no-build --filter 'FullyQualifiedName~MemoryLeakTests' `
        --results-directory $OutDir --logger "trx;LogFileName=$trxName" --logger 'console;verbosity=detailed'

    $suiteSucceeded = $true
    Write-Host "Memory leak suite passed. Results: $(Join-Path $OutDir $trxName)"
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }

    if (-not $suiteSucceeded) {
        $isolatedLogs = Join-Path $isolatedDataRoot 'logs'
        if (Test-Path $isolatedLogs) {
            Copy-Item -LiteralPath $isolatedLogs -Destination (Join-Path $OutDir 'app-logs') -Recurse -Force
        }
    }

    $resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
    if ($resolvedTempRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTempRoot).StartsWith('ConnectOnion-memory-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
