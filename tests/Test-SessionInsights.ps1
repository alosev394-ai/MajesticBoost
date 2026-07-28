[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This test must run in Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ApplicationPath)) {
    $ApplicationPath = Join-Path $projectRoot 'dist\MajesticBoost.exe'
}

if (-not (Test-Path -LiteralPath $ApplicationPath -PathType Leaf)) {
    throw "Application was not found: $ApplicationPath"
}

$assembly = [Reflection.Assembly]::LoadFile(
    [IO.Path]::GetFullPath($ApplicationPath))
$reportType = $assembly.GetType('MajesticBoost.BoostSessionReport', $true)
$performanceType = $assembly.GetType('MajesticBoost.BoostPerformanceResult', $true)
$assistantType = $assembly.GetType('MajesticBoost.BoostCrashAssistant', $true)
$comparisonType = $assembly.GetType('MajesticBoost.BoostSessionComparison', $true)
$categoryType = $assembly.GetType('MajesticBoost.BoostCrashCategory', $true)
$snapshotType = $assembly.GetType('MajesticBoost.DiagnosticSnapshot', $true)
$pressureType = $assembly.GetType('MajesticBoost.DiagnosticPressureLevel', $true)
$storeType = $assembly.GetType('MajesticBoost.BoostSessionReportStore', $true)
$outcomeType = $assembly.GetType('MajesticBoost.BoostActionOutcome', $true)
$windowType = $assembly.GetType('MajesticBoost.BoostWindow', $true)

$bindingFlags = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'

function New-Report {
    param(
        [string]$Id,
        [datetime]$StartedUtc
    )

    $report = [Activator]::CreateInstance($reportType, $true)
    $reportType.GetField('SessionId', $bindingFlags).SetValue($report, $Id)
    $reportType.GetField('StartedUtc', $bindingFlags).SetValue($report, $StartedUtc)
    return $report
}

function Set-Field {
    param(
        [object]$Target,
        [Type]$Type,
        [string]$Name,
        [object]$Value
    )

    $field = $Type.GetField($Name, $bindingFlags)
    if ($null -eq $field) {
        throw "Field not found: $($Type.FullName).$Name"
    }
    $field.SetValue($Target, $Value)
}

$analyzeMethod = $assistantType.GetMethod('Analyze', $bindingFlags)
if ($null -eq $analyzeMethod) {
    throw 'BoostCrashAssistant.Analyze was not found.'
}

$accessReport = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow)
Set-Field $accessReport $reportType 'GameCrashCode' 'c0000005'
Set-Field $accessReport $reportType 'GameCrashModule' 'C:\Users\private\ReShade64.dll'
$accessInsight = $analyzeMethod.Invoke($null, @($accessReport))
$accessCategory = $accessInsight.GetType().GetField('Category', $bindingFlags).GetValue($accessInsight)
if ([string]$accessCategory -cne 'AccessViolation') {
    throw "0xC0000005 was classified as $accessCategory."
}
$accessEvidence = [string]$accessInsight.GetType().GetField('Evidence', $bindingFlags).GetValue($accessInsight)
if ($accessEvidence -notmatch 'ReShade64\.dll' -or $accessEvidence -match 'Users\\private') {
    throw 'Crash evidence must contain only the module file name, not its private path.'
}

$memoryReport = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow)
Set-Field $memoryReport $reportType 'GameCrashCode' '0xC000012D'
$memoryInsight = $analyzeMethod.Invoke($null, @($memoryReport))
$memoryCategory = $memoryInsight.GetType().GetField('Category', $bindingFlags).GetValue($memoryInsight)
if ([string]$memoryCategory -cne 'MemoryPressure') {
    throw "0xC000012D was classified as $memoryCategory."
}

$graphicsReport = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow)
Set-Field $graphicsReport $reportType 'GameCrashCode' '0x887A0006'
$graphicsInsight = $analyzeMethod.Invoke($null, @($graphicsReport))
$graphicsCategory = $graphicsInsight.GetType().GetField('Category', $bindingFlags).GetValue($graphicsInsight)
if ([string]$graphicsCategory -cne 'GraphicsDevice') {
    throw "0x887A0006 was classified as $graphicsCategory."
}

$previous = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow.AddHours(-2))
$current = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow.AddHours(-1))

$previousPerformance = [Activator]::CreateInstance($performanceType, $true)
Set-Field $previousPerformance $performanceType 'Available' $true
Set-Field $previousPerformance $performanceType 'AverageFps' ([double]100)
Set-Field $previousPerformance $performanceType 'OnePercentLowFps' ([double]70)
Set-Field $previousPerformance $performanceType 'P95FrameTimeMs' ([double]15)
Set-Field $previousPerformance $performanceType 'Frames' 1000
Set-Field $previousPerformance $performanceType 'FramesOver50Ms' 12
Set-Field $previousPerformance $performanceType 'ProcessName' 'GTA5.exe'
Set-Field $previous $reportType 'Performance' $previousPerformance

$currentPerformance = [Activator]::CreateInstance($performanceType, $true)
Set-Field $currentPerformance $performanceType 'Available' $true
Set-Field $currentPerformance $performanceType 'AverageFps' ([double]112.5)
Set-Field $currentPerformance $performanceType 'OnePercentLowFps' ([double]78)
Set-Field $currentPerformance $performanceType 'P95FrameTimeMs' ([double]12)
Set-Field $currentPerformance $performanceType 'Frames' 1000
Set-Field $currentPerformance $performanceType 'FramesOver50Ms' 5
Set-Field $currentPerformance $performanceType 'ProcessName' 'gta5'
Set-Field $current $reportType 'Performance' $currentPerformance

$differentGame = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow.AddMinutes(-90))
$differentPerformance = [Activator]::CreateInstance($performanceType, $true)
Set-Field $differentPerformance $performanceType 'Available' $true
Set-Field $differentPerformance $performanceType 'AverageFps' ([double]1)
Set-Field $differentPerformance $performanceType 'OnePercentLowFps' ([double]1)
Set-Field $differentPerformance $performanceType 'P95FrameTimeMs' ([double]999)
Set-Field $differentPerformance $performanceType 'Frames' 1000
Set-Field $differentPerformance $performanceType 'FramesOver50Ms' 999
Set-Field $differentPerformance $performanceType 'ProcessName' 'GTA5_Enhanced.exe'
Set-Field $differentGame $reportType 'Performance' $differentPerformance

$listType = [Collections.Generic.List``1].MakeGenericType($reportType)
$recent = [Activator]::CreateInstance($listType)
[void]$listType.GetMethod('Add').Invoke($recent, @($current))
[void]$listType.GetMethod('Add').Invoke($recent, @($differentGame))
[void]$listType.GetMethod('Add').Invoke($recent, @($previous))

$compareMethod = $comparisonType.GetMethod('Compare', $bindingFlags)
$comparison = $compareMethod.Invoke($null, @($current, $recent))
$comparisonResultType = $comparison.GetType()
if (-not [bool]$comparisonResultType.GetField('Available', $bindingFlags).GetValue($comparison)) {
    throw 'Comparable FPS sessions were not matched.'
}
if ([double]$comparisonResultType.GetField('AverageFpsDelta', $bindingFlags).GetValue($comparison) -ne 12.5) {
    throw 'Average FPS delta is incorrect.'
}
if ([int]$comparisonResultType.GetField('FramesOver50MsDelta', $bindingFlags).GetValue($comparison) -ne -7) {
    throw 'Slow-frame delta is incorrect.'
}
if ([string]$comparisonResultType.GetField('ComparedSessionId', $bindingFlags).GetValue($comparison) -cne
    [string]$reportType.GetField('SessionId', $bindingFlags).GetValue($previous)) {
    throw 'FPS comparison mixed measurements from different tracked game processes.'
}

$resourceReport = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow)
$snapshot = [Activator]::CreateInstance($snapshotType, $true)
Set-Field $snapshot $snapshotType 'MemoryAvailable' $true
Set-Field $snapshot $snapshotType 'PhysicalTotalBytes' ([long](16GB))
Set-Field $snapshot $snapshotType 'PhysicalAvailableBytes' ([long](2GB))
Set-Field $snapshot $snapshotType 'CommitLimitBytes' ([long](24GB))
Set-Field $snapshot $snapshotType 'CommitHeadroomBytes' ([long](1GB))
Set-Field $snapshot $snapshotType 'PageFileAvailable' $true
Set-Field $snapshot $snapshotType 'PageFileAllocatedBytes' ([long](8GB))
Set-Field $snapshot $snapshotType 'PageFileUsedBytes' ([long](3GB))
Set-Field $snapshot $snapshotType 'GpuUsageAvailable' $true
Set-Field $snapshot $snapshotType 'GpuBudgetAvailable' $true
Set-Field $snapshot $snapshotType 'GpuTotalAvailable' $true
Set-Field $snapshot $snapshotType 'GpuDedicatedUsageBytes' ([long](6GB))
Set-Field $snapshot $snapshotType 'GpuDedicatedBudgetBytes' ([long](8GB))
Set-Field $snapshot $snapshotType 'GpuDedicatedTotalBytes' ([long](8GB))
Set-Field $snapshot $snapshotType 'GpuAdapterNames' 'Test GPU'
Set-Field $snapshot $snapshotType 'GpuAdapterLuid' '00000001_00000002'
Set-Field $snapshot $snapshotType 'Pressure' ([Enum]::Parse($pressureType, 'Critical'))

$applySnapshot = $reportType.GetMethod('ApplyDiagnosticSnapshot', $bindingFlags)
$applySnapshot.Invoke($resourceReport, @($snapshot))
if ([int]$reportType.GetField('DiagnosticSamples', $bindingFlags).GetValue($resourceReport) -ne 1 -or
    [int]$reportType.GetField('GpuMemorySamples', $bindingFlags).GetValue($resourceReport) -ne 1 -or
    [long]$reportType.GetField('MinimumGpuDedicatedHeadroomBytes', $bindingFlags).GetValue($resourceReport) -ne 2GB -or
    [string]$reportType.GetField('WorstResourcePressure', $bindingFlags).GetValue($resourceReport) -cne 'Critical') {
    throw 'Session resource telemetry did not preserve its first critical GPU sample.'
}

$lowerPressureAdapter = [Activator]::CreateInstance($snapshotType, $true)
Set-Field $lowerPressureAdapter $snapshotType 'GpuUsageAvailable' $true
Set-Field $lowerPressureAdapter $snapshotType 'GpuTotalAvailable' $true
Set-Field $lowerPressureAdapter $snapshotType 'GpuDedicatedUsageBytes' ([long](4GB))
Set-Field $lowerPressureAdapter $snapshotType 'GpuDedicatedTotalBytes' ([long](16GB))
Set-Field $lowerPressureAdapter $snapshotType 'GpuAdapterNames' 'Other GPU'
Set-Field $lowerPressureAdapter $snapshotType 'GpuAdapterLuid' '00000003_00000004'
$applySnapshot.Invoke($resourceReport, @($lowerPressureAdapter))
if ([string]$reportType.GetField('GpuAdapterLuid', $bindingFlags).GetValue($resourceReport) -cne
        '00000001_00000002' -or
    [long]$reportType.GetField('GpuDedicatedTotalBytes', $bindingFlags).GetValue($resourceReport) -ne 8GB -or
    [long]$reportType.GetField('PeakGpuDedicatedUsageBytes', $bindingFlags).GetValue($resourceReport) -ne 6GB) {
    throw 'A lower-pressure adapter replaced the selected GPU and mixed its capacity.'
}

$higherPressureAdapter = [Activator]::CreateInstance($snapshotType, $true)
Set-Field $higherPressureAdapter $snapshotType 'GpuUsageAvailable' $true
Set-Field $higherPressureAdapter $snapshotType 'GpuTotalAvailable' $true
Set-Field $higherPressureAdapter $snapshotType 'GpuDedicatedUsageBytes' ([long](15GB))
Set-Field $higherPressureAdapter $snapshotType 'GpuDedicatedTotalBytes' ([long](16GB))
Set-Field $higherPressureAdapter $snapshotType 'GpuAdapterNames' 'Other GPU'
Set-Field $higherPressureAdapter $snapshotType 'GpuAdapterLuid' '00000003_00000004'
$applySnapshot.Invoke($resourceReport, @($higherPressureAdapter))
if ([string]$reportType.GetField('GpuAdapterLuid', $bindingFlags).GetValue($resourceReport) -cne
        '00000003_00000004' -or
    [string]$reportType.GetField('GpuAdapterNames', $bindingFlags).GetValue($resourceReport) -cne
        'Other GPU' -or
    [long]$reportType.GetField('GpuDedicatedTotalBytes', $bindingFlags).GetValue($resourceReport) -ne 16GB -or
    [long]$reportType.GetField('PeakGpuDedicatedUsageBytes', $bindingFlags).GetValue($resourceReport) -ne 15GB -or
    [long]$reportType.GetField('MinimumGpuDedicatedHeadroomBytes', $bindingFlags).GetValue($resourceReport) -ne 1GB -or
    [int]$reportType.GetField('GpuMemorySamples', $bindingFlags).GetValue($resourceReport) -ne 1) {
    throw 'A higher-pressure adapter did not replace all linked GPU telemetry atomically.'
}

$serialize = $storeType.GetMethod('Serialize', $bindingFlags)
$deserialize = $storeType.GetMethod('Deserialize', $bindingFlags)
$serialized = [string]$serialize.Invoke($null, @($resourceReport))
$roundTrip = $deserialize.Invoke(
    $null,
    [object[]](,[string[]]($serialized -split "\r?\n")))
if ($null -eq $roundTrip -or
    [int]$reportType.GetField('Version', $bindingFlags).GetValue($roundTrip) -ne 3 -or
    [string]$reportType.GetField('GpuAdapterNames', $bindingFlags).GetValue($roundTrip) -cne 'Other GPU' -or
    [string]$reportType.GetField('GpuAdapterLuid', $bindingFlags).GetValue($roundTrip) -cne '00000003_00000004' -or
    [long]$reportType.GetField('PageFileAllocatedBytes', $bindingFlags).GetValue($roundTrip) -ne 8GB) {
    throw 'Version 3 session telemetry did not survive serialization.'
}

$cloneSource = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow)
$clonePerformance = [Activator]::CreateInstance($performanceType, $true)
Set-Field $clonePerformance $performanceType 'Available' $true
Set-Field $clonePerformance $performanceType 'AverageFps' ([double]60)
Set-Field $cloneSource $reportType 'Performance' $clonePerformance
$addAction = $reportType.GetMethod('AddAction', $bindingFlags)
$changedOutcome = [Enum]::Parse($outcomeType, 'Changed')
$addAction.Invoke($cloneSource, @('FIRST', 'before clone', $changedOutcome))
$cloneMethod = $reportType.GetMethod('Clone', $bindingFlags)
$clonedReport = $cloneMethod.Invoke($cloneSource, @())
Set-Field $clonePerformance $performanceType 'AverageFps' ([double]999)
$addAction.Invoke($cloneSource, @('SECOND', 'after clone', $changedOutcome))
$clonedPerformance = $reportType.GetField(
    'Performance',
    $bindingFlags).GetValue($clonedReport)
$clonedActions = $reportType.GetField(
    'Actions',
    $bindingFlags).GetValue($clonedReport)
if ([double]$performanceType.GetField(
        'AverageFps',
        $bindingFlags).GetValue($clonedPerformance) -ne 60 -or
    [int]$clonedActions.Count -ne 1) {
    throw 'Session report cloning did not isolate mutable performance/actions state.'
}

$mergeMethod = $windowType.GetMethod(
    'MergeExportSessionSnapshot',
    $bindingFlags)
if ($null -eq $mergeMethod) {
    throw 'The bounded in-memory export merge was not compiled.'
}
$activeExport = New-Report -Id 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' -StartedUtc ([datetime]::UtcNow)
Set-Field $activeExport $reportType 'DiagnosticSamples' 7
$staleExport = New-Report -Id 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' -StartedUtc ([datetime]::UtcNow.AddMinutes(-5))
Set-Field $staleExport $reportType 'DiagnosticSamples' 1
$olderExport = New-Report -Id 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' -StartedUtc ([datetime]::UtcNow.AddMinutes(-10))
$storedExports = [Activator]::CreateInstance($listType)
[void]$listType.GetMethod('Add').Invoke($storedExports, @($staleExport))
[void]$listType.GetMethod('Add').Invoke($storedExports, @($olderExport))
$memoryExports = [Activator]::CreateInstance($listType)
[void]$listType.GetMethod('Add').Invoke($memoryExports, @($staleExport))
$mergedExports = $mergeMethod.Invoke(
    $null,
    @($storedExports, $memoryExports, $activeExport))
$firstMerged = $mergedExports[0]
if ($mergedExports.Count -ne 2 -or
    [string]$reportType.GetField(
        'SessionId',
        $bindingFlags).GetValue($firstMerged) -cne
        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' -or
    [int]$reportType.GetField(
        'DiagnosticSamples',
        $bindingFlags).GetValue($firstMerged) -ne 7) {
    throw 'Diagnostic export lost the active session or preferred a stale disk copy.'
}

Write-Host 'Session insight tests passed.'
