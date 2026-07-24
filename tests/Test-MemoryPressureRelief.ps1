[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5 -or
    [IntPtr]::Size -ne 8) {
    throw 'This regression test requires 64-bit Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\MajesticBoost.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$featuresPath = Join-Path $projectRoot 'MajesticBoost\BoostFeatures.cs'
$features = [IO.File]::ReadAllText($featuresPath)

foreach ($required in @(
    'private struct PerformanceInformationNative',
    'private static extern bool GetPerformanceInfo(',
    'public UIntPtr CommitTotal;',
    'public UIntPtr CommitLimit;',
    'public UIntPtr PhysicalTotal;',
    'public UIntPtr PhysicalAvailable;',
    'public UIntPtr SystemCache;',
    'Windows defines PhysicalAvailable as standby + free + zero pages',
    'private static extern IntPtr GetCurrentProcess();',
    'private static extern bool EmptyWorkingSet(IntPtr process);',
    'private static bool TryTrimCurrentProcessWorkingSet(out int errorCode)',
    'EmptyWorkingSet(GetCurrentProcess())',
    'GCCollectionMode.Forced',
    'result.Collected = result.Success;',
    'public const int RequiredCriticalSamples = 2',
    'public const int CooldownSeconds = 600',
    'public const int NoEffectBackoffSeconds = 1800',
    'public const int MaximumAttempts = 3',
    'if (!IsValidSessionId(report.SessionId))',
    'Guid.TryParseExact(sessionId, "N", out parsed)'
)) {
    if (-not $features.Contains($required)) {
        throw "The memory-pressure relief contract is missing: $required"
    }
}

foreach ($forbidden in @(
    'NtSetSystemInformation',
    'MemoryPurgeStandbyList',
    'SetSystemFileCacheSize',
    'AdjustTokenPrivileges',
    'SeDebugPrivilege',
    'OpenProcess('
)) {
    if ($features.Contains($forbidden)) {
        throw "A forbidden system-wide memory API is present: $forbidden"
    }
}

$wrapperStart = $features.IndexOf(
    'private static bool TryTrimCurrentProcessWorkingSet(out int errorCode)',
    [StringComparison]::Ordinal)
$wrapperEnd = $features.IndexOf(
    'private static ActiveMemoryMaintenanceResult NewSkippedResult',
    $wrapperStart,
    [StringComparison]::Ordinal)
if ($wrapperStart -lt 0 -or $wrapperEnd -le $wrapperStart) {
    throw 'The current-process-only working-set wrapper could not be located.'
}
$wrapper = $features.Substring($wrapperStart, $wrapperEnd - $wrapperStart)
if (-not $wrapper.Contains('EmptyWorkingSet(GetCurrentProcess())') -or
    $wrapper.Contains('GetProcessById') -or
    $wrapper.Contains('Process.GetProcesses') -or
    $wrapper.Contains('processId') -or
    $wrapper.Contains('IntPtr process')) {
    throw 'Working-set relief is not restricted to the current process.'
}

$saveStart = $features.IndexOf(
    'public static void Save(BoostSessionReport report)',
    [StringComparison]::Ordinal)
$saveValidation = $features.IndexOf(
    'if (!IsValidSessionId(report.SessionId))',
    $saveStart,
    [StringComparison]::Ordinal)
$savePathConstruction = $features.IndexOf(
    'string sessionPath = Path.Combine(',
    $saveStart,
    [StringComparison]::Ordinal)
if ($saveStart -lt 0 -or
    $saveValidation -le $saveStart -or
    $savePathConstruction -le $saveValidation) {
    throw 'SessionId is not validated before the report path is constructed.'
}

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
$allStatic = [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic -bor
    [Reflection.BindingFlags]::Static
$allInstance = [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic -bor
    [Reflection.BindingFlags]::Instance

$metricsType = $assembly.GetType('MajesticBoost.BoostSystemMetrics', $true)
$snapshotType = $assembly.GetType('MajesticBoost.MemoryPressureSnapshot', $true)
$policyType = $assembly.GetType('MajesticBoost.MemoryPressureReliefPolicy', $true)
$stateType = $assembly.GetType('MajesticBoost.MemoryPressureReliefPolicyState', $true)
$serviceType = $assembly.GetType('MajesticBoost.ActiveMemoryMaintenanceService', $true)
$reportType = $assembly.GetType('MajesticBoost.BoostSessionReport', $true)
$storeType = $assembly.GetType('MajesticBoost.BoostSessionReportStore', $true)

$nativeType = $metricsType.GetNestedType(
    'PerformanceInformationNative',
    [Reflection.BindingFlags]::NonPublic)
if (-not $nativeType) {
    throw 'The native PERFORMANCE_INFORMATION layout is missing.'
}
foreach ($fieldName in @(
    'CommitTotal',
    'CommitLimit',
    'CommitPeak',
    'PhysicalTotal',
    'PhysicalAvailable',
    'SystemCache',
    'KernelTotal',
    'KernelPaged',
    'KernelNonpaged',
    'PageSize'
)) {
    $field = $nativeType.GetField($fieldName, $allInstance)
    if (-not $field -or $field.FieldType -ne [UIntPtr]) {
        throw "Native field $fieldName is not pointer-sized on x64."
    }
}
$sizeOfType = [Runtime.InteropServices.Marshal].GetMethod(
    'SizeOf',
    [Type[]]@([Type]))
$nativeSize = [int]$sizeOfType.Invoke(
    $null,
    [object[]]@($nativeType))
if ($nativeSize -lt 100 -or ($nativeSize % 8) -ne 0) {
    throw "Unexpected x64 PERFORMANCE_INFORMATION size: $nativeSize"
}

function Get-FieldValue {
    param(
        [Type]$Type,
        [object]$Instance,
        [string]$Name
    )
    $field = $Type.GetField($Name, $allInstance)
    if (-not $field) {
        throw "Missing field $($Type.FullName).$Name"
    }
    return $field.GetValue($Instance)
}

function Set-FieldValue {
    param(
        [Type]$Type,
        [object]$Instance,
        [string]$Name,
        [object]$Value
    )
    $field = $Type.GetField($Name, $allInstance)
    if (-not $field) {
        throw "Missing field $($Type.FullName).$Name"
    }
    $field.SetValue($Instance, $Value)
}

$capture = $metricsType.GetMethod('TryGetPerformanceSnapshot', $allStatic)
if (-not $capture) {
    throw 'TryGetPerformanceSnapshot was not compiled.'
}
$captureArguments = New-Object 'object[]' 1
$captured = [bool]$capture.Invoke($null, $captureArguments)
$snapshot = $captureArguments[0]
if (-not $captured -or -not $snapshot) {
    throw 'GetPerformanceInfo did not return a usable snapshot.'
}

$pageSize = [long](Get-FieldValue $snapshotType $snapshot 'PageSizeBytes')
$totalPhysical = [long](Get-FieldValue $snapshotType $snapshot 'TotalPhysicalBytes')
$availablePhysical = [long](Get-FieldValue $snapshotType $snapshot 'AvailablePhysicalBytes')
$systemCache = [long](Get-FieldValue $snapshotType $snapshot 'SystemCacheBytes')
$commitTotal = [long](Get-FieldValue $snapshotType $snapshot 'CommitTotalBytes')
$commitLimit = [long](Get-FieldValue $snapshotType $snapshot 'CommitLimitBytes')
$commitHeadroom = [long](Get-FieldValue $snapshotType $snapshot 'CommitHeadroomBytes')
$processMetrics = [bool](Get-FieldValue $snapshotType $snapshot 'ProcessMetricsAvailable')
if ($pageSize -lt 4096 -or (($pageSize -band ($pageSize - 1)) -ne 0)) {
    throw "Invalid native page size: $pageSize"
}
if ($totalPhysical -le 0 -or
    $availablePhysical -lt 0 -or
    $availablePhysical -gt $totalPhysical -or
    $systemCache -lt 0) {
    throw 'Physical-memory metrics are outside plausible bounds.'
}
if ($commitTotal -le 0 -or
    $commitLimit -le 0 -or
    $commitHeadroom -ne [Math]::Max(0L, $commitLimit - $commitTotal)) {
    throw 'Commit metrics or commit headroom are inconsistent.'
}
if (-not $processMetrics) {
    throw 'Current-process working-set metrics are unavailable.'
}

$evaluate = $policyType.GetMethod('Evaluate', $allStatic)
$recordAttempt = $policyType.GetMethod('RecordAttempt', $allStatic)
$physicalThreshold = $policyType.GetMethod(
    'GetPhysicalCriticalThreshold',
    $allStatic)
if (-not $evaluate -or -not $recordAttempt -or -not $physicalThreshold) {
    throw 'The pure memory-pressure policy API is incomplete.'
}

$gib = 1024L * 1024L * 1024L
$expected16GiBThreshold = [long][Math]::Floor((16L * $gib) / 20.0)
$actual16GiBThreshold = [long]$physicalThreshold.Invoke(
    $null,
    [object[]]@([long](16L * $gib)))
if ($actual16GiBThreshold -ne $expected16GiBThreshold) {
    throw "Unexpected 16-GiB critical threshold: $actual16GiBThreshold"
}

$criticalSnapshot = [Activator]::CreateInstance($snapshotType)
Set-FieldValue $snapshotType $criticalSnapshot 'MetricsAvailable' $true
Set-FieldValue $snapshotType $criticalSnapshot 'TotalPhysicalBytes' ([long](16L * $gib))
Set-FieldValue $snapshotType $criticalSnapshot 'AvailablePhysicalBytes' ([long](512L * 1024L * 1024L))
Set-FieldValue $snapshotType $criticalSnapshot 'CommitLimitBytes' ([long](40L * $gib))
Set-FieldValue $snapshotType $criticalSnapshot 'CommitHeadroomBytes' ([long](10L * $gib))

$state = [Activator]::CreateInstance($stateType)
$now = [Diagnostics.Stopwatch]::GetTimestamp()
$first = $evaluate.Invoke(
    $null,
    [object[]]@($criticalSnapshot, $state, [long]$now))
if ((Get-FieldValue $first.GetType() $first 'Decision').ToString() -cne
    'AwaitingSecondSample') {
    throw 'The first critical sample incorrectly triggered memory relief.'
}
$firstState = Get-FieldValue $first.GetType() $first 'NextState'
$second = $evaluate.Invoke(
    $null,
    [object[]]@($criticalSnapshot, $firstState, [long]($now + 1)))
if ((Get-FieldValue $second.GetType() $second 'Decision').ToString() -cne
    'ReliefRequired') {
    throw 'Two consecutive critical samples did not trigger relief.'
}

$secondState = Get-FieldValue $second.GetType() $second 'NextState'
$backoffState = $recordAttempt.Invoke(
    $null,
    [object[]]@($secondState, [long]$now, [long]0))
$nextAllowed = [long](Get-FieldValue $stateType $backoffState 'NextAllowedTimestamp')
$expectedBackoff = $now + ([Diagnostics.Stopwatch]::Frequency * 1800L)
if ($nextAllowed -ne $expectedBackoff -or
    [int](Get-FieldValue $stateType $backoffState 'Attempts') -ne 1) {
    throw 'A zero-effect attempt did not receive the 30-minute backoff.'
}
$cooldown = $evaluate.Invoke(
    $null,
    [object[]]@($criticalSnapshot, $backoffState, [long]($now + 1)))
if ((Get-FieldValue $cooldown.GetType() $cooldown 'Decision').ToString() -cne
    'Cooldown') {
    throw 'The policy ignored its monotonic cooldown.'
}

$limitState = [Activator]::CreateInstance($stateType)
Set-FieldValue $stateType $limitState 'Attempts' 3
$limited = $evaluate.Invoke(
    $null,
    [object[]]@($criticalSnapshot, $limitState, [long]$now))
if ((Get-FieldValue $limited.GetType() $limited 'Decision').ToString() -cne
    'AttemptLimitReached') {
    throw 'The three-attempt session limit is not enforced.'
}

$classify = $serviceType.GetMethod('ClassifyReliefOutcome', $allStatic)
if (-not $classify) {
    throw 'The truthful relief-outcome classifier is missing.'
}
function Invoke-Classify {
    param(
        [bool]$NativeSucceeded,
        [bool]$AfterMetricsAvailable,
        [long]$ReclaimedBytes
    )
    return $classify.Invoke(
        $null,
        [object[]]@($NativeSucceeded, $AfterMetricsAvailable, $ReclaimedBytes)
    ).ToString()
}
if ((Invoke-Classify $true $true 0) -cne 'NoEffect' -or
    (Invoke-Classify $true $true 1) -cne 'Succeeded' -or
    (Invoke-Classify $false $true 1) -cne 'Failed' -or
    (Invoke-Classify $true $false 1) -cne 'MetricsUnavailable') {
    throw 'Memory relief reports success without measurable reclaimed bytes.'
}

$validateSessionId = $storeType.GetMethod('IsValidSessionId', $allStatic)
$deserializeReport = $storeType.GetMethod('Deserialize', $allStatic)
$saveReport = $storeType.GetMethod('Save', $allStatic)
if (-not $validateSessionId -or -not $deserializeReport -or -not $saveReport) {
    throw 'The secured session-report persistence API is incomplete.'
}
$validSessionId = [Guid]::NewGuid().ToString('N')
if (-not [bool]$validateSessionId.Invoke(
        $null,
        [object[]]@($validSessionId)) -or
    [bool]$validateSessionId.Invoke(
        $null,
        [object[]]@('..\..\Temp\sentinel'))) {
    throw 'SessionId validation is not restricted to GUID N format.'
}

function ConvertTo-ReportBase64 {
    param([string]$Value)
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value))
}
$validLines = [string[]]@(
    'Version=1',
    ('SessionId=' + (ConvertTo-ReportBase64 $validSessionId)),
    ('StartedUtc=' + [DateTime]::UtcNow.ToString('o', [Globalization.CultureInfo]::InvariantCulture))
)
$validDeserialize = $deserializeReport.Invoke(
    $null,
    [object[]](,$validLines))
if (-not $validDeserialize -or
    (Get-FieldValue $reportType $validDeserialize 'SessionId') -cne $validSessionId) {
    throw 'A valid GUID-N session report is no longer compatible.'
}
$invalidLines = [string[]]@(
    'Version=1',
    ('SessionId=' + (ConvertTo-ReportBase64 '..\..\Temp\sentinel')),
    ('StartedUtc=' + [DateTime]::UtcNow.ToString('o', [Globalization.CultureInfo]::InvariantCulture))
)
if ($null -ne $deserializeReport.Invoke($null, [object[]](,$invalidLines))) {
    throw 'Deserialize accepted a path-traversal SessionId.'
}

$sentinelToken = 'MajesticBoost-traversal-' + [Guid]::NewGuid().ToString('N')
$sentinelPath = Join-Path $env:LOCALAPPDATA ('Temp\' + $sentinelToken + '.report')
$sentinelContent = 'sentinel-must-remain-unchanged'
[IO.File]::WriteAllText(
    $sentinelPath,
    $sentinelContent,
    (New-Object Text.UTF8Encoding($false)))
try {
    $maliciousReport = [Activator]::CreateInstance($reportType)
    Set-FieldValue $reportType $maliciousReport 'SessionId' (
        'x\..\..\..\Temp\' + $sentinelToken)
    Set-FieldValue $reportType $maliciousReport 'StartedUtc' ([DateTime]::UtcNow)
    $rejected = $false
    try {
        [void]$saveReport.Invoke($null, [object[]]@($maliciousReport))
    }
    catch {
        $exception = $_.Exception
        while ($exception) {
            if ($exception -is [ArgumentException]) {
                $rejected = $true
                break
            }
            $exception = $exception.InnerException
        }
        if (-not $rejected) {
            throw
        }
    }
    if (-not $rejected) {
        throw 'Save did not reject a path-traversal SessionId.'
    }
    if (-not [IO.File]::Exists($sentinelPath) -or
        [IO.File]::ReadAllText($sentinelPath) -cne $sentinelContent) {
        throw 'The traversal sentinel outside Sessions was modified.'
    }
}
finally {
    if ([IO.File]::Exists($sentinelPath)) {
        [IO.File]::Delete($sentinelPath)
    }
}

# Touch enough pages in this test process to make the current-process-only
# EmptyWorkingSet integration observable without allocating memory elsewhere.
$pressure = New-Object byte[] (96 * 1024 * 1024)
for ($index = 0; $index -lt $pressure.Length; $index += 4096) {
    $pressure[$index] = 1
}
$immediate = $serviceType.GetMethod(
    'RunImmediateForCurrentProcess',
    $allStatic)
if (-not $immediate) {
    throw 'The current-process-only relief entry point was not compiled.'
}
$relief = $immediate.Invoke($null, [object[]]@('Regression test'))
$resultType = $relief.GetType()
$attempted = [bool](Get-FieldValue $resultType $relief 'Attempted')
$nativeSucceeded = [bool](Get-FieldValue $resultType $relief 'NativeCallSucceeded')
$success = [bool](Get-FieldValue $resultType $relief 'Success')
$collected = [bool](Get-FieldValue $resultType $relief 'Collected')
$reclaimed = [long](Get-FieldValue $resultType $relief 'ReclaimedWorkingSetBytes')
$status = (Get-FieldValue $resultType $relief 'Status').ToString()
if (-not $attempted -or -not $nativeSucceeded) {
    throw 'The documented current-process EmptyWorkingSet call failed.'
}
if ($reclaimed -le 0) {
    if ($status -cne 'NoEffect' -or $success -or $collected) {
        throw 'A zero-byte relief attempt was falsely reported as successful.'
    }
}
elseif ($status -cne 'Succeeded' -or -not $success -or -not $collected) {
    throw 'A measurable self-only working-set reduction was not reported honestly.'
}
$pressure = $null

Write-Host (
    'Memory-pressure relief test passed. Available={0:N0} MB; Commit={1:N0}/{2:N0} MB; Self reclaimed={3:N0} MB; Status={4}.' -f
    ($availablePhysical / 1MB),
    ($commitTotal / 1MB),
    ($commitLimit / 1MB),
    ($reclaimed / 1MB),
    $status
) -ForegroundColor Green
