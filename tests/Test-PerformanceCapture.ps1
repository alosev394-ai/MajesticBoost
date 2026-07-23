[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$fixture = Join-Path $PSScriptRoot 'fixtures\presentmon-v2.csv'
$captureSource = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'MajesticBoost\PerformanceCapture.cs'))
$installerSource = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'MajesticBoostInstaller\Program.cs'))
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'MajesticBoost-PerformanceCapture-' + [Guid]::NewGuid().ToString('N'))
$harness = Join-Path $temporaryRoot 'PerformanceCaptureParserHarness.exe'

foreach ($required in @(
    'Environment.SpecialFolder.CommonApplicationData',
    'ResolveProtectedCaptureDirectory()',
    'ValidateProtectedCaptureDirectory',
    'ValidateElevatedCaptureFile(elevatedOutputPath);',
    'MajesticBoost-PresentMon-',
    'FileAttributes.ReparsePoint'
)) {
    if (-not $captureSource.Contains($required)) {
        throw "Protected elevated capture contract is missing: $required"
    }
}

$elevatedPathStart = $captureSource.IndexOf(
    'private static string CreateElevatedCapturePath()',
    [StringComparison]::Ordinal)
$elevatedPathEnd = $captureSource.IndexOf(
    'private static void ValidateProtectedCaptureDirectory',
    $elevatedPathStart,
    [StringComparison]::Ordinal)
if ($elevatedPathStart -lt 0 -or $elevatedPathEnd -le $elevatedPathStart) {
    throw 'The protected elevated capture path section could not be located.'
}
$elevatedPathSection = $captureSource.Substring(
    $elevatedPathStart,
    $elevatedPathEnd - $elevatedPathStart)
if ($elevatedPathSection.Contains('SpecialFolder.Windows') -or
    $elevatedPathSection.Contains('"Temp"')) {
    throw 'Elevated capture must not stage CSV files in Windows\Temp.'
}

foreach ($required in @(
    'PrepareCaptureDirectoryTransaction',
    'ApplyCaptureDirectoryTransaction',
    'RollbackCaptureDirectoryTransaction',
    'SetAccessRuleProtection(true, false)',
    'WellKnownSidType.LocalSystemSid',
    'WellKnownSidType.BuiltinAdministratorsSid',
    'WellKnownSidType.AuthenticatedUserSid',
    'FileSystemRights.ReadAndExecute',
    'FileSystemRights.Delete',
    'PropagationFlags.InheritOnly',
    'Directory.CreateDirectory(path, security)',
    'TryPruneProtectedCaptureFiles(true)'
)) {
    if (-not $installerSource.Contains($required)) {
        throw "Protected capture installer contract is missing: $required"
    }
}

try {
    [void](New-Item -ItemType Directory -Path $temporaryRoot -Force)
    $arguments = @(
        '/nologo',
        '/target:exe',
        '/optimize+',
        '/reference:System.dll',
        '/reference:System.Core.dll',
        "/out:$harness",
        (Join-Path $projectRoot 'MajesticBoost\BoostFeatures.cs'),
        (Join-Path $projectRoot 'MajesticBoost\PerformanceCapture.cs'),
        (Join-Path $PSScriptRoot 'PerformanceCaptureParserHarness.cs')
    )

    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "PerformanceCapture harness compilation failed with exit code $LASTEXITCODE."
    }

    & $harness $fixture
    if ($LASTEXITCODE -ne 0) {
        throw "PerformanceCapture parser test failed with exit code $LASTEXITCODE."
    }
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedTarget = [IO.Path]::GetFullPath($temporaryRoot)
    if (
        $resolvedTarget.StartsWith(
            $resolvedTemp + 'MajesticBoost-PerformanceCapture-',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTarget)
    ) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force -ErrorAction SilentlyContinue
    }
}
