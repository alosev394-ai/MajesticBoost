[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\MajesticBoost.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

$program = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'MajesticBoost\Program.cs'))
$features = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'MajesticBoost\BoostFeatures.cs'))

foreach ($required in @(
    'public const int IntervalSeconds = 60',
    'GCCollectionMode.Forced',
    'GetPerformanceInfo(',
    'EmptyWorkingSet(GetCurrentProcess())',
    'public const int RequiredCriticalSamples = 2',
    'result.Collected = result.Success',
    'ResetPolicyState()'
)) {
    if (-not $features.Contains($required)) {
        throw "The pressure-aware memory contract is missing: $required"
    }
}

foreach ($required in @(
    'nextMemoryMaintenanceTimestamp = Stopwatch.GetTimestamp();',
    'ActiveMemoryMaintenanceService.GetNextDueTimestamp(nowTimestamp);',
    'Interlocked.CompareExchange(ref benchmarkCaptureActive, 0, 0)',
    'ActiveMemoryMaintenanceService.ResetPolicyState();',
    'UpdateSessionMemoryTelemetry(result.Before);',
    'if (!result.Attempted)',
    'if (result.Success)',
    'currentSession.MemoryReliefBytes',
    'result.ReclaimedWorkingSetBytes'
)) {
    if (-not $program.Contains($required)) {
        throw "The Active Boost memory integration is missing: $required"
    }
}

$applicationSources = Get-ChildItem -LiteralPath (
    Join-Path $projectRoot 'MajesticBoost') -Filter '*.cs' -File |
    ForEach-Object { [IO.File]::ReadAllText($_.FullName) }
$combined = [string]::Join([Environment]::NewLine, $applicationSources)
foreach ($forbidden in @(
    'NtSetSystemInformation',
    'MemoryPurgeStandbyList',
    'SetSystemFileCacheSize',
    'SetProcessWorkingSetSize',
    'AdjustTokenPrivileges',
    'OpenProcess(',
    'WaitForPendingFinalizers'
)) {
    if ($combined.Contains($forbidden)) {
        throw "Unsafe system-wide memory manipulation is forbidden: $forbidden"
    }
}

$serviceStart = $features.IndexOf(
    'internal static class ActiveMemoryMaintenanceService',
    [StringComparison]::Ordinal)
$preflightStart = $features.IndexOf(
    'internal static class BoostPreflightService',
    $serviceStart,
    [StringComparison]::Ordinal)
if ($serviceStart -lt 0 -or $preflightStart -le $serviceStart) {
    throw 'Memory service source section could not be located.'
}
$serviceSource = $features.Substring(
    $serviceStart,
    $preflightStart - $serviceStart)
if ($serviceSource.Contains('Process.GetProcessesByName') -or
    $serviceSource.Contains('Process.GetProcessById')) {
    throw 'Memory relief must never trim an external process.'
}

$startMethodStart = $program.IndexOf(
    'private void StartActiveBoostMaintenance',
    [StringComparison]::Ordinal)
$refreshMethodStart = $program.IndexOf(
    'private void RefreshActiveBoostMaintenance',
    $startMethodStart,
    [StringComparison]::Ordinal)
$startMethod = $program.Substring(
    $startMethodStart,
    $refreshMethodStart - $startMethodStart)
if ($startMethod.IndexOf(
        'nextMemoryMaintenanceTimestamp = Stopwatch.GetTimestamp();',
        [StringComparison]::Ordinal) -lt 0 -or
    $startMethod.IndexOf(
        'QueueActiveBoostMaintenance();',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'The first pressure sample is not queued immediately.'
}

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
$serviceType = $assembly.GetType(
    'MajesticBoost.ActiveMemoryMaintenanceService',
    $true)
$flags = [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic -bor
    [Reflection.BindingFlags]::Static
$getNextDue = $serviceType.GetMethod('GetNextDueTimestamp', $flags)
$isDue = $serviceType.GetMethod('IsDue', $flags)
$now = 123456L
$next = [long]$getNextDue.Invoke($null, [object[]]@($now))
$expectedNext = $now + ([Diagnostics.Stopwatch]::Frequency * 60L)
if ($next -ne $expectedNext) {
    throw "The monotonic sampling interval is not 60 seconds: $next"
}
if ([bool]$isDue.Invoke($null, [object[]]@([long]($next - 1), $next)) -or
    -not [bool]$isDue.Invoke($null, [object[]]@($next, $next))) {
    throw 'Monotonic due-time boundary is incorrect.'
}

& (Join-Path $PSScriptRoot 'Test-MemoryPressureRelief.ps1') `
    -ApplicationPath $ApplicationPath
if (-not $?) {
    throw 'Detailed memory pressure relief test failed.'
}

Write-Host 'Pressure-aware Active Boost memory regression test passed.' `
    -ForegroundColor Green
