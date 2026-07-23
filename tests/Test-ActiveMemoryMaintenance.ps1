[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\MajesticBoost.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

$programPath = Join-Path $projectRoot 'MajesticBoost\Program.cs'
$featuresPath = Join-Path $projectRoot 'MajesticBoost\BoostFeatures.cs'
$program = [IO.File]::ReadAllText($programPath)
$features = [IO.File]::ReadAllText($featuresPath)

foreach ($required in @(
    'public const int IntervalSeconds = 120',
    'public const long MinimumAvailableMemoryBytes = 1024L * 1024L * 1024L',
    'public const long ManagedHeapThresholdBytes = 32L * 1024L * 1024L',
    'GCCollectionMode.Optimized',
    'GC.GetTotalMemory(false)'
)) {
    if (-not $features.Contains($required)) {
        throw "The safe memory-maintenance contract is missing: $required"
    }
}

foreach ($required in @(
    'ActiveMemoryMaintenanceService.GetNextDueTimestamp(',
    'Stopwatch.GetTimestamp()',
    'Interlocked.CompareExchange(ref benchmarkCaptureActive, 0, 0)',
    'Interlocked.Increment(ref activeMemoryMaintenanceCycles)',
    'Interlocked.Exchange(ref activeMemoryMaintenanceCycles, 0)',
    'ManagedMemoryMaintenanceCycles'
)) {
    if (-not $program.Contains($required)) {
        throw "The Active Boost integration contract is missing: $required"
    }
}

$applicationSources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'MajesticBoost') -Filter '*.cs' -File |
    ForEach-Object { [IO.File]::ReadAllText($_.FullName) }
$combinedApplicationSource = [string]::Join([Environment]::NewLine, $applicationSources)
foreach ($forbidden in @(
    'NtSetSystemInformation',
    'MemoryPurgeStandbyList',
    'SetSystemFileCacheSize',
    'EmptyWorkingSet',
    'SetProcessWorkingSetSize',
    'WaitForPendingFinalizers'
)) {
    if ($combinedApplicationSource.Contains($forbidden)) {
        throw "Aggressive memory manipulation is forbidden: $forbidden"
    }
}

$maintenanceStart = $program.IndexOf(
    'private void RunActiveBoostMaintenance',
    [StringComparison]::Ordinal)
$restoreStart = $program.IndexOf(
    'private void RestoreOwnedGamePriorities',
    $maintenanceStart,
    [StringComparison]::Ordinal)
if ($maintenanceStart -lt 0 -or $restoreStart -le $maintenanceStart) {
    throw 'The Active Boost maintenance section could not be located.'
}
$maintenance = $program.Substring($maintenanceStart, $restoreStart - $maintenanceStart)
$demoGuard = $maintenance.IndexOf('if (demoMode)', [StringComparison]::Ordinal)
$memoryCall = $maintenance.IndexOf(
    'RunActiveMemoryMaintenance(generation);',
    [StringComparison]::Ordinal)
$gameScan = $maintenance.IndexOf(
    'Process.GetProcessesByName(gameName)',
    [StringComparison]::Ordinal)
if ($demoGuard -lt 0 -or $memoryCall -le $demoGuard -or $gameScan -le $memoryCall) {
    throw 'Memory maintenance must run after the demo guard and before the game scan.'
}
if (-not $program.Contains('Task.Run(delegate')) {
    throw 'Active maintenance must remain off the UI thread.'
}

$startMethodStart = $program.IndexOf(
    'private void StartActiveBoostMaintenance',
    [StringComparison]::Ordinal)
$refreshMethodStart = $program.IndexOf(
    'private void RefreshActiveBoostMaintenance',
    $startMethodStart,
    [StringComparison]::Ordinal)
if ($startMethodStart -lt 0 -or $refreshMethodStart -le $startMethodStart) {
    throw 'The Active Boost start method could not be located.'
}
$startMethod = $program.Substring(
    $startMethodStart,
    $refreshMethodStart - $startMethodStart)
$initialDeadline = $startMethod.IndexOf(
    'ActiveMemoryMaintenanceService.GetNextDueTimestamp(',
    [StringComparison]::Ordinal)
$initialQueue = $startMethod.IndexOf(
    'QueueActiveBoostMaintenance();',
    [StringComparison]::Ordinal)
if ($initialDeadline -lt 0 -or $initialQueue -le $initialDeadline) {
    throw 'The first memory-maintenance deadline must be set before the immediate game scan.'
}

$memoryMethodStart = $program.IndexOf(
    'private void RunActiveMemoryMaintenance',
    [StringComparison]::Ordinal)
$beginSessionStart = $program.IndexOf(
    'private void BeginSession',
    [StringComparison]::Ordinal)
if ($memoryMethodStart -lt 0 -or $beginSessionStart -le $memoryMethodStart) {
    throw 'The memory-maintenance integration method could not be located.'
}
$memoryMethod = $program.Substring(
    $memoryMethodStart,
    $beginSessionStart - $memoryMethodStart)
$reschedule = $memoryMethod.IndexOf(
    'ActiveMemoryMaintenanceService.GetNextDueTimestamp(nowTimestamp);',
    [StringComparison]::Ordinal)
$benchmarkGuard = $memoryMethod.IndexOf(
    'Interlocked.CompareExchange(ref benchmarkCaptureActive, 0, 0)',
    [StringComparison]::Ordinal)
$collection = $memoryMethod.IndexOf(
    'ActiveMemoryMaintenanceService.Run();',
    [StringComparison]::Ordinal)
if ($reschedule -lt 0 -or $benchmarkGuard -le $reschedule -or $collection -le $benchmarkGuard) {
    throw 'A skipped FPS-benchmark cycle must reschedule from now without catch-up.'
}

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
$serviceType = $assembly.GetType(
    'MajesticBoost.ActiveMemoryMaintenanceService',
    $true)
$flags = [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic -bor
    [Reflection.BindingFlags]::Static
$shouldCollect = $serviceType.GetMethod('ShouldCollect', $flags)
$getThreshold = $serviceType.GetMethod('GetAvailableMemoryThreshold', $flags)
$getNextDue = $serviceType.GetMethod('GetNextDueTimestamp', $flags)
$isDue = $serviceType.GetMethod('IsDue', $flags)
foreach ($method in @($shouldCollect, $getThreshold, $getNextDue, $isDue)) {
    if (-not $method) {
        throw 'A testable memory-maintenance method is missing.'
    }
}

$gib = 1024L * 1024L * 1024L
$mib = 1024L * 1024L
function Invoke-ShouldCollect {
    param(
        [long]$Total,
        [long]$Available,
        [long]$Heap
    )
    return [bool]$shouldCollect.Invoke(
        $null,
        [object[]]@($Total, $Available, $Heap))
}

if (Invoke-ShouldCollect -Total (16L * $gib) -Available (3L * $gib) -Heap (10L * $mib)) {
    throw 'Healthy memory must not trigger a collection.'
}
if (-not (Invoke-ShouldCollect -Total (16L * $gib) -Available (2L * $gib) -Heap (10L * $mib))) {
    throw 'The 12.5 percent available-memory boundary must trigger a collection.'
}
if (-not (Invoke-ShouldCollect -Total (4L * $gib) -Available $gib -Heap (10L * $mib))) {
    throw 'The 1 GiB minimum available-memory boundary must trigger a collection.'
}
if (-not (Invoke-ShouldCollect -Total (16L * $gib) -Available (8L * $gib) -Heap (32L * $mib))) {
    throw 'The 32 MiB managed-heap boundary must trigger a collection.'
}
if (Invoke-ShouldCollect -Total 0 -Available 0 -Heap (31L * $mib)) {
    throw 'Unavailable system metrics alone must not trigger a collection.'
}

$threshold = [long]$getThreshold.Invoke($null, [object[]]@([long](16L * $gib)))
if ($threshold -ne (2L * $gib)) {
    throw "Unexpected available-memory threshold: $threshold"
}

$now = 123456L
$next = [long]$getNextDue.Invoke($null, [object[]]@($now))
$expectedNext = $now + ([Diagnostics.Stopwatch]::Frequency * 120L)
if ($next -ne $expectedNext) {
    throw "The first memory-maintenance deadline is not 120 seconds: $next"
}
if ([bool]$isDue.Invoke($null, [object[]]@([long]($next - 1L), $next))) {
    throw 'Memory maintenance became due before its monotonic deadline.'
}
if (-not [bool]$isDue.Invoke($null, [object[]]@($next, $next))) {
    throw 'Memory maintenance did not become due at its monotonic deadline.'
}

Write-Host 'Safe Active Boost memory-maintenance regression test passed.' -ForegroundColor Green
