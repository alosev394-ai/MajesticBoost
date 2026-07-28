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
$sourcePath = Join-Path $projectRoot 'MajesticBoost\DiagnosticsFeatures.cs'
$source = [IO.File]::ReadAllText($sourcePath)
$build = [IO.File]::ReadAllText((Join-Path $projectRoot 'build.ps1'))
if (-not $build.Contains("'/reference:System.Management.dll'") -or
    -not $build.Contains(
        '"$projectRoot\MajesticBoost\DiagnosticsFeatures.cs"')) {
    throw 'The app build does not include diagnostics or System.Management.'
}

foreach ($required in @(
    'internal sealed class DiagnosticSnapshot',
    'internal static class DiagnosticSnapshotProvider',
    'public static DiagnosticSnapshot Capture()',
    'private static extern bool GetPerformanceInfo(',
    'Win32_PageFileUsage',
    'GPU Adapter Memory',
    'CreateDXGIFactory1(',
    'TrySelectMostPressuredAdapter(',
    'GpuAdapterLuidPattern',
    'DedicatedVideoMemory',
    'GpuCapacityBasis=Matching DXGI DedicatedVideoMemory for selected LUID',
    'internal static class DiagnosticPressureClassifier',
    'internal static class DiagnosticSessionHistory',
    'public const int MaximumSessionCount = 10',
    'public static List<BoostSessionReport> LoadRecent(int maximumCount)',
    'internal static class DiagnosticExportBuilder',
    'public static string BuildSafeReport(',
    'public static void WriteSafeReport(',
    'public const int MaximumExportCharacters = 64 * 1024',
    'Guid.TryParseExact(candidate, "N", out parsed)',
    'FileAttributes.ReparsePoint'
)) {
    if (-not $source.Contains($required)) {
        throw "The diagnostic feature contract is missing: $required"
    }
}

foreach ($forbidden in @(
    'EmptyWorkingSet',
    'NtSetSystemInformation',
    'MemoryPurgeStandbyList',
    'SetSystemFileCacheSize',
    'AdjustTokenPrivileges',
    'SeDebugPrivilege',
    'QueryVideoMemoryInfo',
    'DxgiVideoMemoryInformation',
    'Adapter3InterfaceId'
)) {
    if ($source.Contains($forbidden)) {
        throw "Diagnostics must be read-only; forbidden API found: $forbidden"
    }
}

$assembly = [Reflection.Assembly]::Load(
    [IO.File]::ReadAllBytes($ApplicationPath))
$allStatic = [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic -bor
    [Reflection.BindingFlags]::Static
$allInstance = [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic -bor
    [Reflection.BindingFlags]::Instance

$snapshotType = $assembly.GetType('MajesticBoost.DiagnosticSnapshot', $true)
$providerType = $assembly.GetType('MajesticBoost.DiagnosticSnapshotProvider', $true)
$classifierType = $assembly.GetType('MajesticBoost.DiagnosticPressureClassifier', $true)
$historyType = $assembly.GetType('MajesticBoost.DiagnosticSessionHistory', $true)
$exportType = $assembly.GetType('MajesticBoost.DiagnosticExportBuilder', $true)
$overlayType = $assembly.GetType('MajesticBoost.BoostCenterOverlay', $true)

function Set-Field {
    param(
        [Type]$Type,
        [object]$Target,
        [string]$Name,
        [object]$Value
    )
    $field = $Type.GetField($Name, $allInstance)
    if (-not $field) {
        throw "Missing field $($Type.FullName).$Name"
    }
    $field.SetValue($Target, $Value)
}

function Get-Field {
    param(
        [Type]$Type,
        [object]$Target,
        [string]$Name
    )
    $field = $Type.GetField($Name, $allInstance)
    if (-not $field) {
        throw "Missing field $($Type.FullName).$Name"
    }
    return $field.GetValue($Target)
}

function New-Snapshot {
    param(
        [bool]$Available,
        [long]$PhysicalTotal,
        [long]$PhysicalAvailable,
        [long]$CommitUsed,
        [long]$CommitLimit,
        [long]$GpuUsage = 0,
        [long]$GpuTotal = 0
    )
    $snapshot = [Activator]::CreateInstance($snapshotType)
    Set-Field $snapshotType $snapshot 'MemoryAvailable' $Available
    Set-Field $snapshotType $snapshot 'PhysicalTotalBytes' $PhysicalTotal
    Set-Field $snapshotType $snapshot 'PhysicalAvailableBytes' $PhysicalAvailable
    Set-Field $snapshotType $snapshot 'CommitUsedBytes' $CommitUsed
    Set-Field $snapshotType $snapshot 'CommitLimitBytes' $CommitLimit
    Set-Field $snapshotType $snapshot 'CommitHeadroomBytes' (
        [Math]::Max([long]0, $CommitLimit - $CommitUsed))
    Set-Field $snapshotType $snapshot 'GpuUsageAvailable' ($GpuTotal -gt 0)
    Set-Field $snapshotType $snapshot 'GpuBudgetAvailable' $false
    Set-Field $snapshotType $snapshot 'GpuTotalAvailable' ($GpuTotal -gt 0)
    Set-Field $snapshotType $snapshot 'GpuDedicatedUsageBytes' $GpuUsage
    Set-Field $snapshotType $snapshot 'GpuDedicatedBudgetBytes' ([long]0)
    Set-Field $snapshotType $snapshot 'GpuDedicatedTotalBytes' $GpuTotal
    return $snapshot
}

$classify = $classifierType.GetMethod(
    'Classify',
    $allStatic,
    $null,
    [Type[]]@($snapshotType),
    $null)
if (-not $classify) {
    throw 'The pure pressure classifier was not compiled.'
}

$gib = [long](1024 * 1024 * 1024)
$pressureCases = @(
    @{
        Name = 'Unavailable'
        Snapshot = New-Snapshot $false (16 * $gib) (8 * $gib) (8 * $gib) (32 * $gib)
        Expected = 'Unavailable'
    },
    @{
        Name = 'Healthy'
        Snapshot = New-Snapshot $true (16 * $gib) (8 * $gib) (8 * $gib) (32 * $gib)
        Expected = 'Normal'
    },
    @{
        Name = 'Physical elevated'
        Snapshot = New-Snapshot $true (16 * $gib) (2 * $gib) (8 * $gib) (32 * $gib)
        Expected = 'Elevated'
    },
    @{
        Name = 'Physical critical'
        Snapshot = New-Snapshot $true (16 * $gib) (1 * $gib) (8 * $gib) (32 * $gib)
        Expected = 'Critical'
    },
    @{
        Name = 'Commit elevated'
        Snapshot = New-Snapshot $true (16 * $gib) (8 * $gib) (29 * $gib) (32 * $gib)
        Expected = 'Elevated'
    },
    @{
        Name = 'Commit critical'
        Snapshot = New-Snapshot $true (16 * $gib) (8 * $gib) (31 * $gib) (32 * $gib)
        Expected = 'Critical'
    },
    @{
        Name = 'GPU elevated'
        Snapshot = New-Snapshot $true (16 * $gib) (8 * $gib) (8 * $gib) (32 * $gib) 92 100
        Expected = 'Elevated'
    },
    @{
        Name = 'GPU critical'
        Snapshot = New-Snapshot $true (16 * $gib) (8 * $gib) (8 * $gib) (32 * $gib) 96 100
        Expected = 'Critical'
    }
)

foreach ($case in $pressureCases) {
    $actual = $classify.Invoke($null, [object[]]@($case.Snapshot)).ToString()
    if ($actual -cne $case.Expected) {
        throw "$($case.Name) pressure was '$actual', expected '$($case.Expected)'."
    }
}

$legacyBudgetSnapshot = New-Snapshot `
    $true `
    (16 * $gib) `
    (8 * $gib) `
    (8 * $gib) `
    (32 * $gib) `
    96 `
    1000
Set-Field $snapshotType $legacyBudgetSnapshot 'GpuBudgetAvailable' $true
Set-Field $snapshotType $legacyBudgetSnapshot 'GpuDedicatedBudgetBytes' ([long]100)
$legacyBudgetPressure = $classify.Invoke(
    $null,
    [object[]]@($legacyBudgetSnapshot)).ToString()
if ($legacyBudgetPressure -cne 'Normal') {
    throw 'The pressure classifier used a process-specific DXGI budget instead of matching adapter capacity.'
}

$selectAdapter = $providerType.GetMethod(
    'TrySelectMostPressuredAdapter',
    $allStatic)
if (-not $selectAdapter) {
    throw 'The testable per-adapter LUID selector was not compiled.'
}
$selectionArguments = [object[]]@(
    [string[]]@(
        'luid_0x00000000_0x0000000A_phys_0',
        'pid_100_luid_0x00000000_0x0000000A_phys_1',
        'luid_0x00000000_0x0000000B_phys_0',
        'luid_0x00000000_0x0000000C_phys_0',
        'malformed-instance'
    ),
    [long[]]@(6, 2, 45, 999, 5000),
    [string[]]@(
        '000000000000000A',
        '000000000000000B'
    ),
    [long[]]@(10, 50),
    [int]-1,
    [long]0
)
$selectionSucceeded = [bool]$selectAdapter.Invoke(
    $null,
    $selectionArguments)
if (-not $selectionSucceeded -or
    [int]$selectionArguments[4] -ne 1 -or
    [long]$selectionArguments[5] -ne 45) {
    throw 'GPU samples were aggregated across adapters or the highest per-adapter pressure was not selected.'
}

$signedHighArguments = [object[]]@(
    [string[]]@('luid_0xFFFFFFFF_0x12345678_phys_0'),
    [long[]]@(75),
    [string[]]@('FFFFFFFF12345678'),
    [long[]]@(100),
    [int]-1,
    [long]0
)
if (-not [bool]($selectAdapter.Invoke($null, $signedHighArguments)) -or
    [int]$signedHighArguments[4] -ne 0 -or
    [long]$signedHighArguments[5] -ne 75) {
    throw 'GPU LUID parsing did not preserve the signed DXGI high part.'
}

$unmatchedArguments = [object[]]@(
    [string[]]@('luid_0x00000000_0x0000000C_phys_0'),
    [long[]]@(90),
    [string[]]@('000000000000000D'),
    [long[]]@(100),
    [int]-1,
    [long]0
)
if ([bool]($selectAdapter.Invoke($null, $unmatchedArguments)) -or
    [int]$unmatchedArguments[4] -ne -1 -or
    [long]$unmatchedArguments[5] -ne 0) {
    throw 'An unmatched GPU LUID was incorrectly paired with another adapter capacity.'
}

$capture = $providerType.GetMethod('Capture', $allStatic)
if (-not $capture) {
    throw 'DiagnosticSnapshotProvider.Capture was not compiled.'
}
$realSnapshot = $capture.Invoke($null, @())
if (-not [bool](Get-Field $snapshotType $realSnapshot 'MemoryAvailable')) {
    throw 'GetPerformanceInfo did not return a usable diagnostic snapshot.'
}
$physicalTotal = [long](Get-Field $snapshotType $realSnapshot 'PhysicalTotalBytes')
$physicalAvailable = [long](Get-Field $snapshotType $realSnapshot 'PhysicalAvailableBytes')
$commitUsed = [long](Get-Field $snapshotType $realSnapshot 'CommitUsedBytes')
$commitLimit = [long](Get-Field $snapshotType $realSnapshot 'CommitLimitBytes')
$commitHeadroom = [long](Get-Field $snapshotType $realSnapshot 'CommitHeadroomBytes')
if ($physicalTotal -le 0 -or
    $physicalAvailable -lt 0 -or
    $physicalAvailable -gt $physicalTotal -or
    $commitLimit -le 0 -or
    $commitUsed -lt 0 -or
    $commitUsed -gt $commitLimit -or
    $commitHeadroom -ne ($commitLimit - $commitUsed)) {
    throw 'The real memory/commit snapshot is internally inconsistent.'
}

$gpuUsageAvailable = [bool](Get-Field $snapshotType $realSnapshot 'GpuUsageAvailable')
$gpuBudgetAvailable = [bool](Get-Field $snapshotType $realSnapshot 'GpuBudgetAvailable')
$gpuTotalAvailable = [bool](Get-Field $snapshotType $realSnapshot 'GpuTotalAvailable')
$gpuError = [string](Get-Field $snapshotType $realSnapshot 'GpuError')
if ($gpuUsageAvailable -and
    [long](Get-Field $snapshotType $realSnapshot 'GpuDedicatedUsageBytes') -lt 0) {
    throw 'GPU usage was reported available with an invalid value.'
}
if ($gpuBudgetAvailable -or
    [long](Get-Field $snapshotType $realSnapshot 'GpuDedicatedBudgetBytes') -ne 0) {
    throw 'A process-specific DXGI budget was exposed as a system-wide GPU capacity.'
}
if ($gpuUsageAvailable -ne $gpuTotalAvailable) {
    throw 'GPU usage and capacity must only be exposed as a matching LUID pair.'
}
if ($gpuTotalAvailable -and
    ([long](Get-Field $snapshotType $realSnapshot 'GpuDedicatedTotalBytes') -le 0 -or
     [string]::IsNullOrWhiteSpace(
        [string](Get-Field $snapshotType $realSnapshot 'GpuAdapterNames')) -or
     [string]::IsNullOrWhiteSpace(
        [string](Get-Field $snapshotType $realSnapshot 'GpuAdapterLuid')))) {
    throw 'Matched GPU capacity was reported without a valid total, name, or LUID.'
}
if (-not ($gpuUsageAvailable -and $gpuTotalAvailable) -and
    [string]::IsNullOrWhiteSpace($gpuError)) {
    throw 'Gracefully unavailable GPU metrics must include a diagnostic reason.'
}
$pageFileAvailable = [bool](Get-Field $snapshotType $realSnapshot 'PageFileAvailable')
$pageFileError = [string](Get-Field $snapshotType $realSnapshot 'PageFileError')
if ($pageFileAvailable -and
    [long](Get-Field $snapshotType $realSnapshot 'PageFileAllocatedBytes') -le 0) {
    throw 'Page-file metrics were reported available with an invalid allocation.'
}
if (-not $pageFileAvailable -and [string]::IsNullOrWhiteSpace($pageFileError)) {
    throw 'Gracefully unavailable page-file metrics must include a diagnostic reason.'
}

$formatUnavailablePageFile = $overlayType.GetMethod(
    'FormatUnavailablePageFile',
    $allStatic)
if (-not $formatUnavailablePageFile) {
    throw 'The page-file unavailable-state formatter was not compiled.'
}
$providerFailureSnapshot = New-Snapshot $true (16 * $gib) (8 * $gib) (8 * $gib) (32 * $gib)
Set-Field $snapshotType $providerFailureSnapshot 'PageFileAvailable' $false
Set-Field $snapshotType $providerFailureSnapshot 'PageFileError' 'ManagementException.'
$providerFailureState = [string]$formatUnavailablePageFile.Invoke(
    $null,
    [object[]]@($providerFailureSnapshot))
if ([Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($providerFailureState)) -cne
    '0J3QldCU0J7QodCi0KPQn9Cd0J4=') {
    throw 'A page-file provider failure was incorrectly shown as an inactive page file.'
}
$inactivePageFileSnapshot = New-Snapshot $true (16 * $gib) (8 * $gib) (8 * $gib) (32 * $gib)
Set-Field $snapshotType $inactivePageFileSnapshot 'PageFileAvailable' $false
Set-Field $snapshotType $inactivePageFileSnapshot 'PageFileError' 'No active Windows page file was reported.'
$inactivePageFileState = [string]$formatUnavailablePageFile.Invoke(
    $null,
    [object[]]@($inactivePageFileSnapshot))
if ([Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($inactivePageFileState)) -cne
    '0J3QlSDQkNCa0KLQmNCS0JXQnQ==') {
    throw 'A genuinely inactive page file was not labelled as inactive.'
}

function Convert-ToBase64 {
    param([string]$Value)
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value))
}

function New-MinimalReportContent {
    param(
        [string]$SessionId,
        [DateTime]$StartedUtc
    )
    return [string]::Join(
        "`r`n",
        @(
            'Version=2',
            ('SessionId=' + (Convert-ToBase64 $SessionId)),
            ('Trigger=' + (Convert-ToBase64 'Diagnostics test')),
            ('Status=' + (Convert-ToBase64 'Completed')),
            ('StartedUtc=' + $StartedUtc.ToString('o', [Globalization.CultureInfo]::InvariantCulture)),
            'EndedUtc=',
            'AvailableMemoryStartBytes=0',
            'AvailableMemoryEndBytes=0',
            ('GameName=' + (Convert-ToBase64 'GTA5')),
            ('StopReason=' + (Convert-ToBase64 'Test'))
        )) + "`r`n"
}

$loadRecentFromDirectory = $historyType.GetMethod(
    'LoadRecentFromDirectory',
    $allStatic)
if (-not $loadRecentFromDirectory) {
    throw 'The testable bounded history loader was not compiled.'
}

$testRoot = Join-Path $env:TEMP (
    'MajesticBoost-Diagnostics-' + [Guid]::NewGuid().ToString('N'))
try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    $baseTime = [DateTime]::UtcNow.AddHours(-1)
    for ($index = 0; $index -lt 12; $index++) {
        $sessionId = [Guid]::NewGuid().ToString('N')
        $content = New-MinimalReportContent `
            -SessionId $sessionId `
            -StartedUtc $baseTime.AddMinutes($index)
        [IO.File]::WriteAllText(
            (Join-Path $testRoot "session-$sessionId.report"),
            $content,
            (New-Object Text.UTF8Encoding($false)))
    }

    $mismatchedFileId = [Guid]::NewGuid().ToString('N')
    $mismatchedContentId = [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText(
        (Join-Path $testRoot "session-$mismatchedFileId.report"),
        (New-MinimalReportContent $mismatchedContentId ([DateTime]::UtcNow.AddDays(1))),
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'session-..%2Foutside.report'),
        'not a report',
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'session-00000000000000000000000000000000.report.extra'),
        'not a report',
        (New-Object Text.UTF8Encoding($false)))

    $recent = $loadRecentFromDirectory.Invoke(
        $null,
        [object[]]@([string]$testRoot, [int]100))
    if ($recent.Count -ne 10) {
        throw "History returned $($recent.Count) sessions instead of the hard maximum of 10."
    }
    for ($index = 1; $index -lt $recent.Count; $index++) {
        $previous = [DateTime](
            $recent[$index - 1].GetType().GetField(
                'StartedUtc',
                $allInstance).GetValue($recent[$index - 1]))
        $current = [DateTime](
            $recent[$index].GetType().GetField(
                'StartedUtc',
                $allInstance).GetValue($recent[$index]))
        if ($previous -lt $current) {
            throw 'History is not sorted newest-first.'
        }
    }

    $threeRecent = $loadRecentFromDirectory.Invoke(
        $null,
        [object[]]@([string]$testRoot, [int]3))
    if ($threeRecent.Count -ne 3) {
        throw 'The requested history count was not honored.'
    }
    $none = $loadRecentFromDirectory.Invoke(
        $null,
        [object[]]@([string]$testRoot, [int]0))
    if ($none.Count -ne 0) {
        throw 'A zero-length history request returned reports.'
    }

    $buildSafeReport = $exportType.GetMethod('BuildSafeReport', $allStatic)
    $writeSafeReport = $exportType.GetMethod('WriteSafeReport', $allStatic)
    if (-not $buildSafeReport -or -not $writeSafeReport) {
        throw 'The safe diagnostic export API was not compiled.'
    }

    $email = 'private.user@example.com'
    $secret = 'token=do-not-export-this'
    $arbitraryPath = 'C:\Private\SaveGames\profile.dat'
    $notes = @(
        "User=$env:USERNAME",
        "Home=$env:USERPROFILE\Private\profile.dat",
        "Email=$email",
        "Secret=$secret",
        "Path=$arbitraryPath"
    ) -join "`r`n"
    $safeReport = [string]$buildSafeReport.Invoke(
        $null,
        [object[]]@($realSnapshot, $recent, [string]$notes))

    foreach ($sensitive in @(
        $env:USERNAME,
        $env:USERPROFILE,
        $email,
        'do-not-export-this',
        $arbitraryPath
    )) {
        if (-not [string]::IsNullOrWhiteSpace($sensitive) -and
            $safeReport.IndexOf(
                $sensitive,
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Safe export leaked sensitive content: $sensitive"
        }
    }
    if (-not $safeReport.Contains('<email>') -or
        -not $safeReport.Contains('<redacted>') -or
        -not $safeReport.Contains('<path>')) {
        throw 'Safe export did not mark redacted email, secret, and path values.'
    }

    $maximumExportCharacters = [int]$exportType.GetField(
        'MaximumExportCharacters',
        $allStatic).GetRawConstantValue()
    $largeReport = [string]$buildSafeReport.Invoke(
        $null,
        [object[]]@($realSnapshot, $recent, ('X' * ($maximumExportCharacters * 3))))
    if ($largeReport.Length -gt $maximumExportCharacters) {
        throw 'Diagnostic export exceeded its hard size bound.'
    }

    $destination = Join-Path $testRoot 'safe-diagnostic.txt'
    [void]$writeSafeReport.Invoke(
        $null,
        [object[]]@([string]$destination, $realSnapshot, $recent, [string]$notes))
    if (-not (Test-Path -LiteralPath $destination) -or
        (Get-Item -LiteralPath $destination).Length -le 0) {
        throw 'WriteSafeReport did not create a diagnostic text file.'
    }
    $written = [IO.File]::ReadAllText($destination)
    if (-not $written.Contains('MAJESTIC BOOST SAFE DIAGNOSTIC REPORT') -or
        $written.Length -gt $maximumExportCharacters -or
        $written.Contains($email) -or
        $written.Contains('do-not-export-this') -or
        $written.Contains($arbitraryPath)) {
        throw 'WriteSafeReport did not preserve the bounded/redacted export contract.'
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

'Diagnostics snapshot, pressure, history, redaction, and export tests passed.'
