[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
    'Local\SilusSuspect.MajesticBoost.Application',
    'applicationMutex.WaitOne(0, false)',
    'catch (AbandonedMutexException)',
    'applicationMutex.ReleaseMutex()',
    'DispatcherUnhandledException',
    'AppDomain.CurrentDomain.UnhandledException',
    'TaskScheduler.UnobservedTaskException',
    'internal static class CrashLog',
    'MaximumLogBytes = 512 * 1024',
    'TryGetRecentGameCrash(',
    'lastGameSeenUtc.AddSeconds(-10)',
    'IsExpectedCrashProcessId(',
    'entry.InstanceId != 1000',
    '0xC0000005',
    '0xC0000017',
    'BoostSessionReportStore.WriteAllTextAtomic('
)) {
    if (-not $program.Contains($required)) {
        throw "Application reliability contract is missing: $required"
    }
}

foreach ($required in @(
    'public int MemorySamples;',
    'public long MemoryReliefBytes;',
    'public long MinimumCommitHeadroomBytes;',
    'public long PeakGamePrivateBytes;',
    'public string GameCrashCode;',
    '(version != 1 && version != 2)',
    'public static void WriteAllTextAtomic('
)) {
    if (-not $features.Contains($required)) {
        throw "Session reliability contract is missing: $required"
    }
}

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
$allStatic = [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic -bor
    [Reflection.BindingFlags]::Static
$windowType = $assembly.GetType('MajesticBoost.BoostWindow', $true)
$indexedKey = $windowType.GetMethod('IsIndexedResultKey', $allStatic)
if (-not $indexedKey) {
    throw 'The structured result key parser was not compiled.'
}

function Test-IndexedKey {
    param([string]$Key, [string]$Prefix)
    return [bool]$indexedKey.Invoke(
        $null,
        [object[]]@([string]$Key, [string]$Prefix))
}

foreach ($case in @(
    @{ Key = 'Process1'; Prefix = 'Process'; Expected = $true },
    @{ Key = 'PROCESS27'; Prefix = 'Process'; Expected = $true },
    @{ Key = 'StoppedProcessCount'; Prefix = 'Process'; Expected = $false },
    @{ Key = 'ProcessCount'; Prefix = 'Process'; Expected = $false },
    @{ Key = 'Process01'; Prefix = 'Process'; Expected = $false },
    @{ Key = 'Warning1'; Prefix = 'Warning'; Expected = $true },
    @{ Key = 'WarningCount'; Prefix = 'Warning'; Expected = $false },
    @{ Key = 'Warning0'; Prefix = 'Warning'; Expected = $false },
    @{ Key = 'WarningX'; Prefix = 'Warning'; Expected = $false }
)) {
    $actual = Test-IndexedKey `
        -Key ([string]$case.Key) `
        -Prefix ([string]$case.Prefix)
    if ($actual -ne $case.Expected) {
        throw "Unexpected result-key classification for $($case.Key): $actual"
    }
}

$parseCrashProcessId = $windowType.GetMethod(
    'TryParseCrashProcessId',
    $allStatic)
if (-not $parseCrashProcessId) {
    throw 'The crash-event process-id parser was not compiled.'
}

foreach ($case in @(
    @{ Value = '0x2f0'; ExpectedResult = $true; ExpectedId = 752 },
    @{ Value = '752'; ExpectedResult = $true; ExpectedId = 752 },
    @{ Value = ' 0X000002F0 '; ExpectedResult = $true; ExpectedId = 752 },
    @{ Value = '0x0'; ExpectedResult = $false; ExpectedId = 0 },
    @{ Value = 'not-a-pid'; ExpectedResult = $false; ExpectedId = 0 }
)) {
    $arguments = New-Object 'System.Object[]' 2
    $arguments.SetValue([string]$case.Value, 0)
    $arguments.SetValue([int]0, 1)
    $actualResult = [bool]$parseCrashProcessId.Invoke($null, $arguments)
    $actualId = [int]$arguments[1]
    if ($actualResult -ne $case.ExpectedResult -or
        ($actualResult -and $actualId -ne $case.ExpectedId)) {
        throw "Unexpected crash PID parse for '$($case.Value)': result=$actualResult id=$actualId"
    }
}

$matchCrashProcessId = $windowType.GetMethod(
    'IsExpectedCrashProcessId',
    $allStatic)
if (-not $matchCrashProcessId) {
    throw 'The crash-event process-id matcher was not compiled.'
}

$expectedPids = New-Object 'System.Collections.Generic.HashSet[int]'
[void]$expectedPids.Add(752)
foreach ($case in @(
    @{ Expected = $expectedPids; Value = '0x2f0'; Result = $true },
    @{ Expected = $expectedPids; Value = '752'; Result = $true },
    @{ Expected = $expectedPids; Value = '753'; Result = $false },
    @{ Expected = $expectedPids; Value = ''; Result = $false },
    @{ Expected = $expectedPids; Value = 'not-a-pid'; Result = $false },
    @{ Expected = (New-Object 'System.Collections.Generic.HashSet[int]'); Value = ''; Result = $true },
    @{ Expected = $null; Value = ''; Result = $true }
)) {
    $matchArguments = New-Object 'System.Object[]' 2
    if ($null -eq $case.Expected) {
        $matchArguments.SetValue($null, 0)
    }
    else {
        $matchArguments.SetValue(
            ([System.Collections.Generic.ISet[int]]$case.Expected),
            0)
    }
    $matchArguments.SetValue([string]$case.Value, 1)
    $actual = [bool]$matchCrashProcessId.Invoke(
        $null,
        $matchArguments)
    if ($actual -ne $case.Result) {
        throw "Unexpected crash PID match for '$($case.Value)': $actual"
    }
}

$storeType = $assembly.GetType('MajesticBoost.BoostSessionReportStore', $true)
$atomicWrite = $storeType.GetMethod('WriteAllTextAtomic', $allStatic)
if (-not $atomicWrite) {
    throw 'Atomic settings writer was not compiled.'
}
$testRoot = Join-Path $env:TEMP (
    'MajesticBoost-AppReliability-' + [Guid]::NewGuid().ToString('N'))
try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    [string]$destination = Join-Path $testRoot 'boost-preferences.ini'
    $writeArguments = New-Object 'System.Object[]' 2
    $writeArguments.SetValue([string]$destination, 0)
    $writeArguments.SetValue([string]"AutoBoost=True`r`n", 1)
    [void]$atomicWrite.Invoke($null, $writeArguments)
    if ([IO.File]::ReadAllText($destination) -cne "AutoBoost=True`r`n") {
        throw 'Atomic settings writer produced unexpected content.'
    }
    $leftovers = Get-ChildItem -LiteralPath $testRoot -Force |
        Where-Object { $_.Name -ne 'boost-preferences.ini' }
    if ($leftovers) {
        throw 'Atomic settings writer left temporary or backup files.'
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

'Application single-instance, result parsing, telemetry, and crash-safety tests passed.'
